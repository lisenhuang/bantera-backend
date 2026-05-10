using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BanteraApi.Database.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BanteraApi.Chat;

public class ChatPushNotificationService(
    HttpClient httpClient,
    IOptions<ApnsSettings> settingsOptions,
    ILogger<ChatPushNotificationService> logger)
{
    private readonly ApnsSettings _settings = settingsOptions.Value;

    public async Task SendAsync(
        IEnumerable<UserPushToken> tokens,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        CancellationToken cancellationToken = default,
        DateTimeOffset? expiresAt = null)
    {
        var activeTokens = tokens
            .Where(t => !string.IsNullOrWhiteSpace(t.Token))
            .GroupBy(t => (Token: t.Token.Trim(), t.IsSandbox))
            .Select(group => group.First())
            .ToList();

        if (activeTokens.Count == 0)
            return;

        if (!HasConfiguration())
        {
            logger.LogInformation(
                "[ChatPush] Skipping APNs send because configuration is incomplete. Tokens={Count}",
                activeTokens.Count);
            return;
        }

        var providerToken = CreateProviderToken();
        foreach (var pushToken in activeTokens)
        {
            var effectiveSandbox = _settings.EffectiveSandbox(pushToken.IsSandbox);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                BuildEndpoint(pushToken.Token, effectiveSandbox));
            request.Version = new Version(2, 0);
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", providerToken);
            request.Headers.TryAddWithoutValidation("apns-topic", _settings.BundleId);
            request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            request.Headers.TryAddWithoutValidation("apns-priority", "10");
            if (expiresAt is not null)
                request.Headers.TryAddWithoutValidation(
                    "apns-expiration",
                    expiresAt.Value.ToUnixTimeSeconds().ToString());
            request.Content = JsonContent.Create(BuildPayload(title, body, data));

            try
            {
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "[ChatPush] APNs send succeeded. Status={Status} Routing=TokenSandbox EnvironmentModeIgnored={EnvironmentMode} TokenSandbox={TokenSandbox} EffectiveSandbox={EffectiveSandbox} Endpoint={Endpoint} TokenSuffix={TokenSuffix} ThreadId={ThreadId} ThreadType={ThreadType}",
                        (int)response.StatusCode,
                        _settings.EnvironmentMode,
                        pushToken.IsSandbox,
                        effectiveSandbox,
                        BuildHost(effectiveSandbox),
                        TokenSuffix(pushToken.Token),
                        data.TryGetValue("threadId", out var successThreadId) ? successThreadId : null,
                        data.TryGetValue("threadType", out var successThreadType) ? successThreadType : null);
                    continue;
                }

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "[ChatPush] APNs send failed. Status={Status} Routing=TokenSandbox EnvironmentModeIgnored={EnvironmentMode} TokenSandbox={TokenSandbox} EffectiveSandbox={EffectiveSandbox} Endpoint={Endpoint} TokenSuffix={TokenSuffix} ThreadId={ThreadId} ThreadType={ThreadType} Body={Body}",
                    (int)response.StatusCode,
                    _settings.EnvironmentMode,
                    pushToken.IsSandbox,
                    effectiveSandbox,
                    BuildHost(effectiveSandbox),
                    TokenSuffix(pushToken.Token),
                    data.TryGetValue("threadId", out var failedThreadId) ? failedThreadId : null,
                    data.TryGetValue("threadType", out var failedThreadType) ? failedThreadType : null,
                    responseBody);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "[ChatPush] APNs send threw. Routing=TokenSandbox EnvironmentModeIgnored={EnvironmentMode} TokenSandbox={TokenSandbox} EffectiveSandbox={EffectiveSandbox} Endpoint={Endpoint} TokenSuffix={TokenSuffix} ThreadId={ThreadId} ThreadType={ThreadType}",
                    _settings.EnvironmentMode,
                    pushToken.IsSandbox,
                    effectiveSandbox,
                    BuildHost(effectiveSandbox),
                    TokenSuffix(pushToken.Token),
                    data.TryGetValue("threadId", out var thrownThreadId) ? thrownThreadId : null,
                    data.TryGetValue("threadType", out var thrownThreadType) ? thrownThreadType : null);
            }
        }
    }

    private bool HasConfiguration() => _settings.HasConfiguration;

    private static Uri BuildEndpoint(string token, bool isSandbox)
    {
        return new Uri($"{BuildHost(isSandbox)}/3/device/{token}");
    }

    private static string BuildHost(bool isSandbox) =>
        isSandbox ? ApnsSettings.SandboxEndpoint : ApnsSettings.ProductionEndpoint;

    private static string TokenSuffix(string token)
    {
        var normalized = token.Trim();
        return normalized.Length <= 8 ? normalized : normalized[^8..];
    }

    private static Dictionary<string, object?> BuildPayload(
        string title,
        string body,
        IReadOnlyDictionary<string, string> data)
    {
        var payload = new Dictionary<string, object?>
        {
            ["aps"] = new
            {
                alert = new { title, body },
                sound = "default",
            },
        };

        foreach (var pair in data)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
                payload[pair.Key] = pair.Value;
        }

        return payload;
    }

    private string CreateProviderToken()
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(NormalizePem(_settings.PrivateKeyPem!).ToCharArray());

        var signingKey = new ECDsaSecurityKey(ecdsa)
        {
            KeyId = _settings.KeyId,
        };
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256)
        {
            // Bypass the global provider cache so each call uses a fresh ECDsa instance.
            // Without this, a cached provider can hold a reference to a previously disposed ECDsa.
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = new JwtHeader(credentials)
        {
            ["kid"] = _settings.KeyId!,
        };
        var payload = new JwtPayload
        {
            { "iss", _settings.TeamId! },
            { "iat", now },
        };

        try
        {
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
        }
        finally
        {
            ecdsa.Dispose();
        }
    }

    private static string NormalizePem(string pem)
    {
        return pem.Replace("\\n", "\n").Trim();
    }
}

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
        CancellationToken cancellationToken = default)
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
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                BuildEndpoint(pushToken.Token, pushToken.IsSandbox));
            request.Version = new Version(2, 0);
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", providerToken);
            request.Headers.TryAddWithoutValidation("apns-topic", _settings.BundleId);
            request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            request.Headers.TryAddWithoutValidation("apns-priority", "10");
            request.Content = JsonContent.Create(new
            {
                aps = new
                {
                    alert = new { title, body },
                    sound = "default",
                },
                threadId = data.TryGetValue("threadId", out var threadId) ? threadId : null,
                threadType = data.TryGetValue("threadType", out var threadType) ? threadType : null,
            });

            try
            {
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                    continue;

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "[ChatPush] APNs send failed. Status={Status} Sandbox={Sandbox} Body={Body}",
                    (int)response.StatusCode,
                    pushToken.IsSandbox,
                    responseBody);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[ChatPush] APNs send threw for sandbox={Sandbox}.", pushToken.IsSandbox);
            }
        }
    }

    private bool HasConfiguration()
    {
        return !string.IsNullOrWhiteSpace(_settings.KeyId)
            && !string.IsNullOrWhiteSpace(_settings.TeamId)
            && !string.IsNullOrWhiteSpace(_settings.BundleId)
            && !string.IsNullOrWhiteSpace(_settings.PrivateKeyPem);
    }

    private static Uri BuildEndpoint(string token, bool isSandbox)
    {
        var host = isSandbox ? "https://api.sandbox.push.apple.com" : "https://api.push.apple.com";
        return new Uri($"{host}/3/device/{token}");
    }

    private string CreateProviderToken()
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(NormalizePem(_settings.PrivateKeyPem!).ToCharArray());

        var signingKey = new ECDsaSecurityKey(ecdsa)
        {
            KeyId = _settings.KeyId,
        };
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256);
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

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private static string NormalizePem(string pem)
    {
        return pem.Replace("\\n", "\n").Trim();
    }
}

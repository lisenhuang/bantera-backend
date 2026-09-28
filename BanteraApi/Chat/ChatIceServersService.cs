using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BanteraApi.Chat;

public sealed class ChatIceServersService(
    HttpClient httpClient,
    IOptions<CloudflareTurnSettings> options,
    ILogger<ChatIceServersService> logger)
{
    public async Task<ChatIceServersResponse> GetAsync(CancellationToken cancellationToken = default, bool relayOnly = false)
    {
        // Relay-only must never silently fall back to a direct connection.
        var fallback = relayOnly ? new ChatIceServersResponse([], "relay")
            : ChatRealtimeService.BuildDefaultIceServersResponse();
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            if (settings.Enabled)
                logger.LogWarning("Cloudflare TURN configuration is incomplete or invalid; returning fallback ICE configuration.");
            return fallback;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://rtc.live.cloudflare.com/v1/turn/keys/{settings.KeyId}/credentials/generate-ice-servers");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
            request.Content = JsonContent.Create(new { ttl = settings.CredentialTtlSeconds });
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Never log the provider body: it may contain credentials.
                logger.LogWarning("Cloudflare TURN credential generation returned HTTP {StatusCode}; returning fallback ICE configuration.",
                    (int)response.StatusCode);
                return fallback;
            }

            var generated = await response.Content.ReadFromJsonAsync<ChatIceServersResponse>(cancellationToken);
            if (generated?.IceServers is not { Count: > 0 } servers
                || servers.Any(server => !IsValid(server))
                || !servers.Any(server => server.Urls.Any(IsTurnUrl)))
            {
                logger.LogWarning("Cloudflare TURN returned an invalid ICE configuration; returning fallback ICE configuration.");
                return fallback;
            }

            // Preserve the existing STUN service alongside Cloudflare's relays.
            // WebRTC's default ICE policy still permits direct peer connections.
            if (relayOnly)
                return new ChatIceServersResponse(servers
                    .Select(s => s with { Urls = s.Urls.Where(IsTurnUrl).ToArray() })
                    .Where(s => s.Urls.Count > 0).ToArray(), "relay");
            return new ChatIceServersResponse([.. fallback.IceServers, .. servers]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Cloudflare TURN credential request timed out; returning fallback ICE configuration.");
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Cloudflare TURN credential request failed; returning fallback ICE configuration.");
        }
        catch (JsonException)
        {
            logger.LogWarning("Cloudflare TURN credential response was malformed; returning fallback ICE configuration.");
        }

        return fallback;
    }

    private static bool IsValid(ChatIceServerEntryResponse? server) =>
        server?.Urls is { Count: > 0 }
        && server.Urls.All(url => !string.IsNullOrWhiteSpace(url)
            && (url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) || IsTurnUrl(url)))
        && (!server.Urls.Any(IsTurnUrl)
            || (!string.IsNullOrWhiteSpace(server.Username) && !string.IsNullOrWhiteSpace(server.Credential)));

    private static bool IsTurnUrl(string url) =>
        url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase);
}

using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;

namespace BanteraApi.OpenAi;

public sealed record ChatGptDeviceStart(string Attempt, string UserCode, string VerificationUri, int IntervalSeconds, DateTimeOffset ExpiresAt);
public sealed record ChatGptDevicePending(string Attempt, string DeviceAuthId, string UserCode, string BrowserHash,
    Guid Admin, DateTimeOffset ExpiresAt, DateTimeOffset NextPollAt, int IntervalSeconds);
public sealed record ChatGptDeviceCredential(string AccountId, string? Email, string AccessToken, string RefreshToken,
    DateTimeOffset ExpiresAt, DateTimeOffset ConnectedAt);

public sealed partial class ChatGptConnection
{
    // Public Codex OAuth client identifier, not a secret. This integration uses the Codex
    // subscription transport, not the separately registered hosted SIWC/API transport.
    public const string CodexClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string DeviceCallback = "https://auth.openai.com/deviceauth/callback";

    public async Task<ChatGptDeviceStart> StartDeviceAsync(Guid admin, string binding, CancellationToken ct)
    {
        RequireDeviceReady();
        if (!ValidBinding(binding)) throw new ChatGptConnectionException("invalid_callback");
        await using var gate = await LockAsync(ct);
        using var response = await http.PostAsJsonAsync(Issuer + "/api/accounts/deviceauth/usercode", new { client_id = CodexClientId }, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new ChatGptConnectionException("device_auth_disabled");
        EnsureDeviceSuccess(response);
        var data = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var id = Text(data, "device_auth_id"); var code = Text(data, "user_code");
        if (code == "") code = Text(data, "usercode");
        if (id.Length is 0 or > 2048 || code.Length is 0 or > 128) throw new ChatGptConnectionException("provider_failed");
        var interval = data.TryGetProperty("interval", out var intervalValue) && int.TryParse(intervalValue.ToString(), out var seconds)
            ? Math.Clamp(seconds, 5, 30) : 5;
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10);
        var pending = new ChatGptDevicePending(RandomValue(), id, code, Hash(binding), admin, expiry,
            DateTimeOffset.UtcNow.AddSeconds(interval), interval);
        Write("device-pending-" + admin.ToString("N"), pending);
        return new(pending.Attempt, code, "https://auth.openai.com/codex/device", interval, expiry);
    }

    public async Task<string> PollDeviceAsync(Guid admin, string binding, string attempt, CancellationToken ct)
    {
        RequireDeviceReady();
        if (!ValidBinding(binding) || !ValidBinding(attempt)) throw new ChatGptConnectionException("invalid_callback");
        await using var gate = await LockAsync(ct);
        var name = "device-pending-" + admin.ToString("N");
        var p = Read<ChatGptDevicePending>(name);
        if (p is null || p.Admin != admin || p.BrowserHash != Hash(binding) || p.Attempt != attempt)
            throw new ChatGptConnectionException("invalid_callback");
        if (p.ExpiresAt <= DateTimeOffset.UtcNow) { File.Delete(PathFor(name)); return "expired"; }
        if (p.NextPollAt > DateTimeOffset.UtcNow) return "pending";
        Write(name, p with { NextPollAt = DateTimeOffset.UtcNow.AddSeconds(p.IntervalSeconds) });
        using var response = await http.PostAsJsonAsync(Issuer + "/api/accounts/deviceauth/token",
            new { device_auth_id = p.DeviceAuthId, user_code = p.UserCode }, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return "pending";
        if (response.StatusCode == HttpStatusCode.TooManyRequests) {
            Write(name, p with { NextPollAt = DateTimeOffset.UtcNow.AddSeconds(30) });
            return "pending";
        }
        EnsureDeviceSuccess(response);
        var data = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var code = Text(data, "authorization_code"); var verifier = Text(data, "code_verifier");
        if (code == "" || verifier == "") throw new ChatGptConnectionException("provider_failed");
        File.Delete(PathFor(name)); // Single-use even if exchange fails; never replay a redeemed code.
        var tokens = await DeviceTokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = DeviceCallback }, ct);
        Write("codex-account", ParseDeviceCredential(tokens, null));
        return "connected";
    }

    public async Task CancelDeviceAsync(Guid admin, string binding, string attempt, CancellationToken ct)
    {
        RequireDeviceReady();
        await using var gate = await LockAsync(ct);
        var name = "device-pending-" + admin.ToString("N"); var p = Read<ChatGptDevicePending>(name);
        if (p?.Attempt == attempt && p.BrowserHash == Hash(binding)) File.Delete(PathFor(name));
    }

    // For server-side provider requests only; credentials must never be HTTP response DTOs.
    public async Task<ChatGptDeviceCredential> GetDeviceCredentialAsync(CancellationToken ct)
    {
        RequireDeviceReady();
        await using var gate = await LockAsync(ct);
        var account = Read<ChatGptDeviceCredential>("codex-account") ?? throw new ChatGptConnectionException("reconnect_required");
        if (account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return account;
        var data = await DeviceTokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = account.RefreshToken }, ct);
        account = ParseDeviceCredential(data, account);
        Write("codex-account", account); // Serialised across containers sharing the persistent volume.
        return account;
    }

    private async Task<JsonElement> DeviceTokenAsync(Dictionary<string,string> values, CancellationToken ct)
    {
        values["client_id"] = CodexClientId;
        using var response = await http.PostAsync(Issuer + "/oauth/token", new FormUrlEncodedContent(values), ct);
        EnsureDeviceSuccess(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static ChatGptDeviceCredential ParseDeviceCredential(JsonElement data, ChatGptDeviceCredential? previous)
    {
        var access = Text(data, "access_token"); var refresh = NullText(data, "refresh_token") ?? previous?.RefreshToken;
        if (access == "" || string.IsNullOrEmpty(refresh)) throw new ChatGptConnectionException("provider_failed");
        // Claims are read only from tokens obtained directly over TLS from OpenAI, never submitted
        // by a browser. They identify the provider account; they do not authenticate a Bantera user.
        var handler = new JwtSecurityTokenHandler();
        var identityText = Text(data, "id_token");
        var identity = handler.CanReadToken(identityText) ? handler.ReadJwtToken(identityText) : null;
        var accessJwt = handler.CanReadToken(access) ? handler.ReadJwtToken(access) : null;
        var auth = identity?.Claims.FirstOrDefault(c => c.Type == "https://api.openai.com/auth")?.Value
            ?? accessJwt?.Claims.FirstOrDefault(c => c.Type == "https://api.openai.com/auth")?.Value;
        var accountId = previous?.AccountId ?? "";
        if (auth is not null) { using var doc = JsonDocument.Parse(auth); accountId = Text(doc.RootElement, "chatgpt_account_id"); }
        if (string.IsNullOrEmpty(accountId) || previous is not null && previous.AccountId != accountId)
            throw new ChatGptConnectionException("account_mismatch");
        var expiry = data.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt32(out var n) && n > 0
            ? DateTimeOffset.UtcNow.AddSeconds(n) : accessJwt is not null ? new DateTimeOffset(accessJwt.ValidTo, TimeSpan.Zero) : DateTimeOffset.MinValue;
        if (expiry <= DateTimeOffset.UtcNow) throw new ChatGptConnectionException("provider_failed");
        return new(accountId, identity?.Claims.FirstOrDefault(c => c.Type == "email")?.Value ?? previous?.Email,
            access, refresh, expiry, previous?.ConnectedAt ?? DateTimeOffset.UtcNow);
    }

    private void RequireDeviceReady() { if (!config.DeviceReady) throw new ChatGptConnectionException("storage_required"); }
    private static void EnsureDeviceSuccess(HttpResponseMessage response) {
        if (!response.IsSuccessStatusCode) throw new ChatGptConnectionException(response.StatusCode switch {
            HttpStatusCode.TooManyRequests => "quota_exceeded",
            HttpStatusCode.Unauthorized => "reconnect_required",
            _ => "provider_failed"
        });
    }
}

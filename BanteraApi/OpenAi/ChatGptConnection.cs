using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BanteraApi.OpenAi;

public sealed class ChatGptConnectionOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string TokenEndpointAuthMethod { get; set; } = "none";
    public string RedirectUri { get; set; } = "https://bantera.app/dashboard/ai/chatgpt/callback";
    public bool PlanAccessApproved { get; set; }
    public string EncryptionKey { get; set; } = "";
    public string StorageDirectory { get; set; } = "";

    public bool DeviceReady {
        get {
            try { return Path.IsPathFullyQualified(StorageDirectory) && Convert.FromBase64String(EncryptionKey).Length == 32; }
            catch (FormatException) { return false; }
        }
    }

    public bool Ready {
        get {
            try {
                return ClientId.StartsWith("oaiapp_", StringComparison.Ordinal) && PlanAccessApproved &&
                    Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                    uri.AbsolutePath == "/dashboard/ai/chatgpt/callback" && uri.Query == "" && uri.Fragment == "" && uri.UserInfo == "" &&
                    Path.IsPathFullyQualified(StorageDirectory) && Convert.FromBase64String(EncryptionKey).Length == 32 &&
                    (TokenEndpointAuthMethod == "none" || TokenEndpointAuthMethod == "client_secret_basic" && !string.IsNullOrWhiteSpace(ClientSecret));
            } catch (FormatException) { return false; }
        }
    }
}

public sealed class ChatGptConnectionException(string code) : Exception(code)
{
    public string Code { get; } = code;
    public string? ProviderCode { get; init; }
    public int? HttpStatus { get; init; }
    public string? Stage { get; init; }
    public bool SearchCompleted { get; init; }
    public int AnswerCharacters { get; init; }
}
public sealed record ChatGptStatus(bool Configured, bool Connected, bool PlanEnabled, string? Email = null, DateTimeOffset? ConnectedAt = null);
public sealed record ChatGptPending(string State, string Verifier, string Nonce, string BrowserHash, Guid Admin,
    string ClientId, string RedirectUri, DateTimeOffset ExpiresAt);
public sealed record ChatGptCredential(string ClientId, string Subject, string? Email, string AccessToken,
    string? RefreshToken, string[] Scopes, DateTimeOffset ExpiresAt, DateTimeOffset ConnectedAt);

/// <summary>Admin provider connection, separate from Bantera user authentication. Tokens never leave the server.</summary>
public sealed partial class ChatGptConnection(HttpClient http, IOptions<ChatGptConnectionOptions> options)
{
    private const string Issuer = "https://auth.openai.com";
    private const string Resource = "https://api.openai.com/v1";
    private const string PlanScope = "chatgpt.tokens.use.direct";
    private readonly ChatGptConnectionOptions config = options.Value;
    public static string RandomValue() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool ValidBinding(string value) => value.Length is >= 32 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public async Task<ChatGptStatus> StatusAsync(CancellationToken ct)
    {
        if (config.DeviceReady && !config.Ready) {
            await using var deviceGate = await LockAsync(ct);
            var device = Read<ChatGptDeviceCredential>("codex-account");
            return new(true, device is not null, device is not null, device?.Email, device?.ConnectedAt);
        }
        if (!config.Ready) return new(false, false, false);
        await using var gate = await LockAsync(ct);
        var account = Read<ChatGptCredential>("account");
        return new(true, account is not null, account?.Scopes.Contains(PlanScope) == true, account?.Email, account?.ConnectedAt);
    }

    public async Task<string> StartAsync(Guid admin, string browserBinding, CancellationToken ct)
    {
        RequireReady();
        if (!ValidBinding(browserBinding)) throw new ChatGptConnectionException("invalid_callback");
        var discovery = await DiscoveryAsync(ct);
        await using var gate = await LockAsync(ct);
        // Bound pending transactions: a new attempt replaces this admin's earlier attempt.
        DeletePending(admin);
        var state = RandomValue();
        var pending = new ChatGptPending(state, RandomValue(), RandomValue(), Hash(browserBinding), admin,
            config.ClientId, config.RedirectUri, DateTimeOffset.UtcNow.AddMinutes(10));
        Write("pending-" + admin.ToString("N"), pending);
        var hostPath = Path.Combine(config.StorageDirectory, "host-id");
        if (!File.Exists(hostPath)) AtomicWrite(hostPath, "urn:uuid:" + Guid.NewGuid());
        return QueryHelpers.AddQueryString(Endpoint(discovery, "authorization_endpoint"), new Dictionary<string, string?> {
            ["client_id"] = config.ClientId, ["redirect_uri"] = config.RedirectUri, ["response_type"] = "code",
            ["scope"] = "openid profile email offline_access resource.invoke " + PlanScope, ["resource"] = Resource,
            ["state"] = state, ["nonce"] = pending.Nonce, ["code_challenge_method"] = "S256",
            ["code_challenge"] = Hash(pending.Verifier), ["ext_agent_host_id"] = File.ReadAllText(hostPath)
        });
    }

    public async Task CompleteAsync(Guid admin, string browserBinding, string state, string? code, string? error, CancellationToken ct)
    {
        RequireReady();
        if (!ValidBinding(browserBinding) || !ValidBinding(state)) throw new ChatGptConnectionException("invalid_callback");
        await using var gate = await LockAsync(ct);
        var name = "pending-" + admin.ToString("N");
        var pending = Read<ChatGptPending>(name);
        if (pending is null || !Matches(pending, admin, browserBinding, state, config, DateTimeOffset.UtcNow))
            throw new ChatGptConnectionException("invalid_callback");
        File.Delete(PathFor(name)); // Consume before contacting the provider, including denied attempts.
        if (!string.IsNullOrEmpty(error)) throw new ChatGptConnectionException("access_denied");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 8192) throw new ChatGptConnectionException("invalid_callback");
        var discovery = await DiscoveryAsync(ct);
        var tokens = await TokenAsync(Endpoint(discovery, "token_endpoint"), new() {
            ["grant_type"] = "authorization_code", ["code"] = code,
            ["code_verifier"] = pending.Verifier, ["redirect_uri"] = pending.RedirectUri, ["resource"] = Resource
        }, ct);
        var keys = new JsonWebKeySet(await http.GetStringAsync(Endpoint(discovery, "jwks_uri"), ct));
        var identity = ValidateIdentity(Text(tokens, "id_token"), keys.GetSigningKeys(), config.ClientId, pending.Nonce);
        var scopes = Text(tokens, "scope").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains(PlanScope) || Text(tokens, "access_token") == "" || Text(tokens, "token_type").ToLowerInvariant() != "bearer")
            throw new ChatGptConnectionException("plan_permission_missing");
        var previous = Read<ChatGptCredential>("account");
        if (previous is not null && (previous.ClientId != config.ClientId || previous.Subject != identity.Subject))
            throw new ChatGptConnectionException("account_mismatch");
        Write("account", new ChatGptCredential(config.ClientId, identity.Subject, identity.Email,
            Text(tokens, "access_token"), NullText(tokens, "refresh_token"), scopes,
            DateTimeOffset.UtcNow.AddSeconds(Lifetime(tokens)), DateTimeOffset.UtcNow));
    }

    public static bool Matches(ChatGptPending pending, Guid admin, string binding, string state,
        ChatGptConnectionOptions config, DateTimeOffset now) => pending.Admin == admin && pending.State == state &&
        pending.BrowserHash == Hash(binding) && pending.ExpiresAt > now && pending.ClientId == config.ClientId && pending.RedirectUri == config.RedirectUri;

    public static (string Subject, string? Email) ValidateIdentity(string token, IEnumerable<SecurityKey> keys, string clientId, string nonce)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var user = handler.ValidateToken(token, new TokenValidationParameters {
            ValidateIssuer = true, ValidIssuer = Issuer, ValidateAudience = true, ValidAudience = clientId,
            ValidateIssuerSigningKey = true, IssuerSigningKeys = keys, ValidateLifetime = true,
            RequireExpirationTime = true, RequireSignedTokens = true, ClockSkew = TimeSpan.FromSeconds(5),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256]
        }, out _);
        var subject = user.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject) || user.FindFirst("nonce")?.Value != nonce || user.FindFirst("iat") is null)
            throw new SecurityTokenValidationException("Invalid identity claims.");
        return (subject, user.FindFirst("email")?.Value);
    }

    /// <summary>For backend provider adapters only. Never return this value from an HTTP endpoint.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        RequireReady();
        await using var gate = await LockAsync(ct);
        var account = Read<ChatGptCredential>("account");
        if (account is null || account.ClientId != config.ClientId || !account.Scopes.Contains(PlanScope))
            throw new ChatGptConnectionException("reconnect_required");
        if (account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return account.AccessToken;
        if (account.RefreshToken is null) throw new ChatGptConnectionException("reconnect_required");
        var discovery = await DiscoveryAsync(ct);
        var tokens = await TokenAsync(Endpoint(discovery, "token_endpoint"), new() {
            ["grant_type"] = "refresh_token", ["refresh_token"] = account.RefreshToken, ["resource"] = Resource
        }, ct);
        var scopes = tokens.TryGetProperty("scope", out _) ? Text(tokens, "scope").Split(' ', StringSplitOptions.RemoveEmptyEntries) : account.Scopes;
        if (!scopes.Contains(PlanScope) || Text(tokens, "access_token") == "" || Text(tokens, "token_type").ToLowerInvariant() != "bearer")
            throw new ChatGptConnectionException("reconnect_required");
        account = account with { AccessToken = Text(tokens, "access_token"), RefreshToken = NullText(tokens, "refresh_token") ?? account.RefreshToken,
            Scopes = scopes, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Lifetime(tokens)) };
        Write("account", account); // Atomically persist rotating tokens while holding the cross-process lock.
        return account.AccessToken;
    }

    public async Task<bool> DisconnectAsync(Guid admin, CancellationToken ct)
    {
        if (config.DeviceReady && !config.Ready) {
            await using var deviceGate = await LockAsync(ct);
            foreach (var file in Directory.GetFiles(config.StorageDirectory, "device-pending-*.enc")) File.Delete(file);
            File.Delete(PathFor("codex-account"));
            return false; // Local disconnect; no unverified promise of provider-side revocation.
        }
        RequireReady();
        await using var gate = await LockAsync(ct);
        var account = Read<ChatGptCredential>("account");
        var revoked = account?.RefreshToken is null;
        try {
            if (account?.RefreshToken is { } refresh) {
                var discovery = await DiscoveryAsync(ct);
                using var request = FormRequest(Endpoint(discovery, "revocation_endpoint"), new() {
                    ["token"] = refresh, ["token_type_hint"] = "refresh_token"
                }, account.ClientId);
                using var response = await http.SendAsync(request, ct);
                revoked = response.IsSuccessStatusCode;
            }
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ChatGptConnectionException) { revoked = false; }
        // Disconnect also cancels all outstanding attempts to replace the shared connection.
        foreach (var file in Directory.GetFiles(config.StorageDirectory, "pending-*.enc")) File.Delete(file);
        File.Delete(PathFor("account"));
        return revoked;
    }

    private void RequireReady() { if (!config.Ready) throw new ChatGptConnectionException("setup_required"); }
    private async Task<JsonElement> DiscoveryAsync(CancellationToken ct)
    {
        var data = await http.GetFromJsonAsync<JsonElement>(Issuer + "/.well-known/openid-configuration", ct);
        if (Text(data, "issuer") != Issuer) throw new ChatGptConnectionException("provider_failed");
        return data;
    }
    private static string Endpoint(JsonElement discovery, string key)
    {
        var value = Text(discovery, key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "auth.openai.com" || !uri.IsDefaultPort || uri.UserInfo != "")
            throw new ChatGptConnectionException("provider_failed");
        return value;
    }
    private HttpRequestMessage FormRequest(string url, Dictionary<string, string> values, string? clientId = null)
    {
        clientId ??= config.ClientId;
        values["client_id"] = clientId;
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(values) };
        if (config.TokenEndpointAuthMethod == "client_secret_basic") {
            var encoded = System.Net.WebUtility.UrlEncode(clientId) + ":" + System.Net.WebUtility.UrlEncode(config.ClientSecret);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(encoded)));
        }
        return request;
    }
    private async Task<JsonElement> TokenAsync(string url, Dictionary<string, string> values, CancellationToken ct)
    {
        using var request = FormRequest(url, values);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ChatGptConnectionException("provider_failed");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
    private static int Lifetime(JsonElement value) => value.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt32(out var result) && result is > 0 and <= 86400 ? result : throw new ChatGptConnectionException("provider_failed");
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
    private static string? NullText(JsonElement value, string key) => Text(value, key) is { Length: > 0 } s ? s : null;
    private string PathFor(string name) => Path.Combine(config.StorageDirectory, name + ".enc");
    private void DeletePending(Guid admin) => File.Delete(PathFor("pending-" + admin.ToString("N")));
    private T? Read<T>(string name) {
        var path = PathFor(name);
        if (!File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(Unseal(File.ReadAllBytes(path), name, Convert.FromBase64String(config.EncryptionKey)));
    }
    private void Write<T>(string name, T value) => AtomicWrite(PathFor(name), Convert.ToBase64String(Seal(JsonSerializer.SerializeToUtf8Bytes(value), name, Convert.FromBase64String(config.EncryptionKey))));
    public static byte[] Seal(byte[] plain, string purpose, byte[] key) {
        var result = new byte[28 + plain.Length]; RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(result.AsSpan(0, 12), plain, result.AsSpan(28), result.AsSpan(12, 16), Encoding.UTF8.GetBytes(purpose));
        return result;
    }
    public static byte[] Unseal(byte[] file, string purpose, byte[] key) {
        var data = Convert.FromBase64String(Encoding.UTF8.GetString(file));
        if (data.Length < 28) throw new CryptographicException();
        var result = new byte[data.Length - 28]; using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), result, Encoding.UTF8.GetBytes(purpose));
        return result;
    }
    private static void AtomicWrite(string path, string value) {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temp, fileOptions)) {
                var data = Encoding.UTF8.GetBytes(value); file.Write(data); file.Flush(true);
            }
            File.Move(temp, path, true);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private async Task<FileStream> LockAsync(CancellationToken ct) {
        Directory.CreateDirectory(config.StorageDirectory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(config.StorageDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        for (var attempt = 0; ; attempt++) {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(config.StorageDirectory, "connection.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(100, ct); }
        }
    }
}

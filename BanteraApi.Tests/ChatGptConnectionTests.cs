using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BanteraApi.OpenAi;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BanteraApi.Tests;

public sealed class ChatGptConnectionTests
{
    [Fact]
    public async Task MissingRegistrationDoesNotStartOAuthOrCreateStorage()
    {
        var options = new ChatGptConnectionOptions();
        using var http = new HttpClient(new Provider());
        var service = new ChatGptConnection(http, Options.Create(options));
        Assert.False((await service.StatusAsync(default)).Configured);
        var error = await Assert.ThrowsAsync<ChatGptConnectionException>(() => service.StartAsync(Guid.NewGuid(), ChatGptConnection.RandomValue(), default));
        Assert.Equal("setup_required", error.Code);
    }

    [Theory]
    [InlineData("dynamic_agent_client", true, "https://bantera.app/dashboard/ai/chatgpt/callback", false)]
    [InlineData("oaiapp_test", false, "https://bantera.app/dashboard/ai/chatgpt/callback", false)]
    [InlineData("oaiapp_test", true, "http://bantera.app/dashboard/ai/chatgpt/callback", false)]
    [InlineData("oaiapp_test", true, "https://bantera.app/dashboard/ai/chatgpt/callback", true)]
    public void ConfigurationRequiresRegisteredClientAndApprovedWebCallback(string client, bool approved, string callback, bool ready)
    {
        var config = Config(Path.GetTempPath()); config.ClientId = client; config.PlanAccessApproved = approved; config.RedirectUri = callback;
        Assert.Equal(ready, config.Ready);
        config.TokenEndpointAuthMethod = "client_secret_basic";
        Assert.False(config.Ready);
    }

    [Fact]
    public void PendingAttemptIsBoundToAdminBrowserClientCallbackAndExpiry()
    {
        var config = Config(Path.GetTempPath()); var now = DateTimeOffset.UtcNow; var admin = Guid.NewGuid();
        var p = new ChatGptPending("state", "verifier", "nonce", ChatGptConnection.Hash("browser"), admin, config.ClientId, config.RedirectUri, now.AddMinutes(1));
        Assert.True(ChatGptConnection.Matches(p, admin, "browser", "state", config, now));
        Assert.False(ChatGptConnection.Matches(p, Guid.NewGuid(), "browser", "state", config, now));
        Assert.False(ChatGptConnection.Matches(p, admin, "other", "state", config, now));
        Assert.False(ChatGptConnection.Matches(p, admin, "browser", "other", config, now));
        Assert.False(ChatGptConnection.Matches(p, admin, "browser", "state", config, now.AddMinutes(2)));
        Assert.False(ChatGptConnection.Matches(p with { ClientId = "oaiapp_other" }, admin, "browser", "state", config, now));
    }

    [Fact]
    public void EncryptionRejectsTamperingWrongKeysAndPurpose()
    {
        var key = RandomNumberGenerator.GetBytes(32); var plain = Encoding.UTF8.GetBytes("private-token");
        var blob = ChatGptConnection.Seal(plain, "account", key);
        byte[] FileData(byte[] bytes) => Encoding.UTF8.GetBytes(Convert.ToBase64String(bytes));
        Assert.Equal(plain, ChatGptConnection.Unseal(FileData(blob), "account", key));
        Assert.ThrowsAny<CryptographicException>(() => ChatGptConnection.Unseal(FileData(blob), "pending", key));
        Assert.ThrowsAny<CryptographicException>(() => ChatGptConnection.Unseal(FileData(blob), "account", RandomNumberGenerator.GetBytes(32)));
        blob[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => ChatGptConnection.Unseal(FileData(blob), "account", key));
    }

    [Fact]
    public void IdentityRejectsWrongNonceAudienceIssuerSignatureAndExpiry()
    {
        using var provider = new Provider(); var token = provider.Identity("nonce");
        var keys = new[] { provider.Key };
        Assert.Equal("account-1", ChatGptConnection.ValidateIdentity(token, keys, "oaiapp_test", "nonce").Subject);
        Assert.ThrowsAny<SecurityTokenException>(() => ChatGptConnection.ValidateIdentity(token, keys, "oaiapp_test", "other"));
        Assert.ThrowsAny<SecurityTokenException>(() => ChatGptConnection.ValidateIdentity(token, keys, "other", "nonce"));
        Assert.ThrowsAny<SecurityTokenException>(() => ChatGptConnection.ValidateIdentity(provider.Identity("nonce", "https://evil.test"), keys, "oaiapp_test", "nonce"));
        using var other = new Provider();
        Assert.ThrowsAny<SecurityTokenException>(() => ChatGptConnection.ValidateIdentity(token, [other.Key], "oaiapp_test", "nonce"));
        Assert.ThrowsAny<SecurityTokenException>(() => ChatGptConnection.ValidateIdentity(provider.Identity("nonce", expired: true), keys, "oaiapp_test", "nonce"));
    }

    [Fact]
    public async Task FullFlowConsumesStateStoresOnlyEncryptedTokensAndDisconnects()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bantera-oauth-" + Guid.NewGuid());
        try {
            using var provider = new Provider { ExpirySeconds = 30 }; using var http = new HttpClient(provider);
            var service = new ChatGptConnection(http, Options.Create(Config(dir)));
            var admin = Guid.NewGuid(); var binding = ChatGptConnection.RandomValue();
            var url = new Uri(await service.StartAsync(admin, binding, default));
            var query = QueryHelpers.ParseQuery(url.Query);
            provider.Nonce = query["nonce"].ToString(); provider.Challenge = query["code_challenge"].ToString();
            var state = query["state"].ToString();
            await Assert.ThrowsAsync<ChatGptConnectionException>(() => service.CompleteAsync(admin, ChatGptConnection.RandomValue(), state, "code", null, default));
            await service.CompleteAsync(admin, binding, state, "code", null, default);
            Assert.True((await service.StatusAsync(default)).PlanEnabled);
            var refreshed = await Task.WhenAll(service.GetAccessTokenAsync(default), service.GetAccessTokenAsync(default));
            Assert.All(refreshed, value => Assert.Equal("refreshed-access", value));
            Assert.Equal(1, provider.Refreshes);
            Assert.DoesNotContain("private-access", File.ReadAllText(Path.Combine(dir, "account.enc")));
            await Assert.ThrowsAsync<ChatGptConnectionException>(() => service.CompleteAsync(admin, binding, state, "code", null, default));
            Assert.Equal(1, provider.Exchanges);
            Assert.True(await service.DisconnectAsync(admin, default));
            Assert.False((await service.StatusAsync(default)).Connected);
        } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledOrIdentityOnlyAttemptCannotConnect(bool denied)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bantera-oauth-" + Guid.NewGuid());
        try {
            using var provider = new Provider { PlanGranted = false }; using var http = new HttpClient(provider);
            var service = new ChatGptConnection(http, Options.Create(Config(dir))); var admin = Guid.NewGuid(); var binding = ChatGptConnection.RandomValue();
            var query = QueryHelpers.ParseQuery(new Uri(await service.StartAsync(admin, binding, default)).Query);
            provider.Nonce = query["nonce"].ToString(); provider.Challenge = query["code_challenge"].ToString();
            await Assert.ThrowsAsync<ChatGptConnectionException>(() => service.CompleteAsync(admin, binding, query["state"].ToString(), "code", denied ? "access_denied" : null, default));
            Assert.False((await service.StatusAsync(default)).Connected);
            Assert.Empty(Directory.GetFiles(dir, "pending-*.enc"));
            Assert.Equal(denied ? 0 : 1, provider.Exchanges);
        } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static ChatGptConnectionOptions Config(string dir) => new() { ClientId = "oaiapp_test", PlanAccessApproved = true,
        EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), StorageDirectory = dir };
    private sealed class Provider : HttpMessageHandler
    {
        private readonly RSA rsa = RSA.Create(2048);
        public RsaSecurityKey Key { get; }
        public string Nonce = ""; public string Challenge = ""; public int Exchanges; public int Refreshes; public int ExpirySeconds = 3600; public bool PlanGranted = true;
        public Provider() { Key = new RsaSecurityKey(rsa) { KeyId = "test" }; }
        public string Identity(string nonce, string issuer = "https://auth.openai.com", bool expired = false) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer, "oaiapp_test", [new Claim("sub", "account-1"), new Claim("email", "test@example.com"), new Claim("nonce", nonce),
                new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)],
            DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(expired ? -5 : 5), new SigningCredentials(Key, SecurityAlgorithms.RsaSha256)));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object body;
            switch (request.RequestUri!.AbsolutePath) {
                case "/.well-known/openid-configuration": body = new { issuer = "https://auth.openai.com", authorization_endpoint = "https://auth.openai.com/authorize", token_endpoint = "https://auth.openai.com/token", jwks_uri = "https://auth.openai.com/jwks", revocation_endpoint = "https://auth.openai.com/revoke" }; break;
                case "/jwks": var p = rsa.ExportParameters(false); body = new { keys = new[] { new { kty = "RSA", kid = "test", n = Base64UrlEncoder.Encode(p.Modulus!), e = Base64UrlEncoder.Encode(p.Exponent!) } } }; break;
                case "/token":
                    var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
                    if (form["grant_type"] == "refresh_token") {
                        Refreshes++;
                        Assert.Equal("private-refresh", form["refresh_token"]);
                        body = new { access_token = "refreshed-access", refresh_token = "rotated-refresh", token_type = "Bearer", expires_in = 3600 }; break;
                    }
                    Exchanges++;
                    Assert.Equal(Challenge, ChatGptConnection.Hash(form["code_verifier"].ToString()));
                    body = new { id_token = Identity(Nonce), access_token = "private-access", refresh_token = "private-refresh", token_type = "Bearer", expires_in = ExpirySeconds, scope = PlanGranted ? "openid chatgpt.tokens.use.direct" : "openid" }; break;
                case "/revoke": return new HttpResponseMessage(HttpStatusCode.OK);
                default: throw new InvalidOperationException("Unexpected request");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing) { if (disposing) rsa.Dispose(); base.Dispose(disposing); }
    }
}

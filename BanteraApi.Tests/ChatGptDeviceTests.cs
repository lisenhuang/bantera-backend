using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BanteraApi.OpenAi;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public sealed class ChatGptDeviceTests
{
    [Fact]
    public async Task SecureStorageIsRequiredButHostedClientRegistrationIsNot()
    {
        using var env = new Fixture();
        Assert.False(env.Config.Ready);
        Assert.True((await env.Service.StatusAsync(default)).Configured);
        env.Config.EncryptionKey = "";
        Assert.False((await env.Service.StatusAsync(default)).Configured);
        var error = await Assert.ThrowsAsync<ChatGptConnectionException>(() => env.Service.StartDeviceAsync(env.Admin, env.Binding, default));
        Assert.Equal("storage_required", error.Code);
    }

    [Fact]
    public async Task ApprovalPersistsEncryptedCredentialAndDoesNotExposeDeviceSecret()
    {
        using var env = new Fixture(); var start = await env.Start();
        Assert.Equal("https://auth.openai.com/codex/device", start.VerificationUri);
        Assert.DoesNotContain("secret-device-id", JsonSerializer.Serialize(start));
        Assert.Equal("pending", await env.Poll(start));
        Assert.Equal(0, env.Provider.Polls); // Honour provider polling interval.
        env.ReadyToPoll();
        Assert.Equal("connected", await env.Poll(start));
        Assert.True((await env.Service.StatusAsync(default)).Connected);
        Assert.Equal("account-123", (await env.Service.GetDeviceCredentialAsync(default)).AccountId);
        Assert.DoesNotContain("private-refresh", File.ReadAllText(Path.Combine(env.Directory, "codex-account.enc")));
        await Assert.ThrowsAsync<ChatGptConnectionException>(() => env.Poll(start)); // Consumed.
    }

    [Fact]
    public async Task PollRequiresOriginalAdminBrowserAndAttempt()
    {
        using var env = new Fixture(); var start = await env.Start(); env.ReadyToPoll();
        await Assert.ThrowsAsync<ChatGptConnectionException>(() => env.Service.PollDeviceAsync(Guid.NewGuid(), env.Binding, start.Attempt, default));
        await Assert.ThrowsAsync<ChatGptConnectionException>(() => env.Service.PollDeviceAsync(env.Admin, ChatGptConnection.RandomValue(), start.Attempt, default));
        await Assert.ThrowsAsync<ChatGptConnectionException>(() => env.Service.PollDeviceAsync(env.Admin, env.Binding, ChatGptConnection.RandomValue(), default));
        Assert.Equal(0, env.Provider.Polls);
        Assert.Equal("connected", await env.Poll(start));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task WaitingAndThrottlingDoNotCreateAccount(HttpStatusCode status)
    {
        using var env = new Fixture(); env.Provider.PollStatus = status;
        var start = await env.Start(); env.ReadyToPoll();
        Assert.Equal("pending", await env.Poll(start));
        Assert.False((await env.Service.StatusAsync(default)).Connected);
        Assert.Equal("pending", await env.Poll(start)); Assert.Equal(1, env.Provider.Polls);
    }

    [Fact]
    public async Task ExpiryCancelAndDisconnectPreventLateApproval()
    {
        using var env = new Fixture(); var start = await env.Start(); env.ReadyToPoll(expired: true);
        Assert.Equal("expired", await env.Poll(start)); Assert.Equal(0, env.Provider.Polls);
        start = await env.Start(); await env.Service.CancelDeviceAsync(env.Admin, env.Binding, start.Attempt, default);
        await Assert.ThrowsAsync<ChatGptConnectionException>(() => env.Poll(start));
        start = await env.Start(); await env.Service.DisconnectAsync(env.Admin, default);
        await Assert.ThrowsAsync<ChatGptConnectionException>(() => env.Poll(start));
    }

    [Fact]
    public async Task RefreshRotationIsSerialisedAcrossServiceInstancesAndSurvivesRestart()
    {
        using var env = new Fixture(); var start = await env.Start(); env.ReadyToPoll(); env.Provider.ShortExpiry = true;
        await env.Poll(start);
        var second = new ChatGptConnection(env.Http, Options.Create(env.Config));
        var credentials = await Task.WhenAll(env.Service.GetDeviceCredentialAsync(default), second.GetDeviceCredentialAsync(default));
        Assert.Equal(1, env.Provider.Refreshes);
        Assert.All(credentials, c => Assert.Equal("rotated-refresh", c.RefreshToken));
        Assert.True((await second.StatusAsync(default)).Connected);
    }

    [Fact]
    public void ModelChoicesUseProviderCapabilitiesAndExcludeHiddenModels()
    {
        using var doc = JsonDocument.Parse("""{"models":[{"slug":"new-model","display_name":"New","supported_reasoning_levels":[{"effort":"low"},{"effort":"max"}],"default_reasoning_level":"max"},{"slug":"hidden","visibility":"hide"},{"slug":"other","supported_reasoning_levels":["medium"]}]}""");
        var models = ChatGptSubscriptionClient.ParseModels(doc.RootElement);
        Assert.Equal(2, models.Count); Assert.Equal(new[] { "low", "max" }, models[0].ReasoningLevels);
        Assert.Equal("max", models[0].DefaultReasoning); Assert.Equal(new[] { "medium" }, models[1].ReasoningLevels);
    }

    [Fact]
    public async Task StreamingReplyRequiresCompletionAndRealSearchEvidence()
    {
        var stream = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\n";
        await Assert.ThrowsAsync<ChatGptConnectionException>(() => Parse(stream));
        var reply = await Parse(stream + "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n");
        Assert.Equal("Hello", reply.Answer); Assert.False(reply.SearchVerified);
        var searched = await Parse("""
        data: {"type":"response.completed","response":{"status":"completed","output":[{"type":"web_search_call","status":"completed"},{"type":"message","content":[{"type":"output_text","text":"Found","annotations":[{"type":"url_citation","url":"https://example.com","title":"Source"},{"type":"url_citation","url":"javascript:bad()","title":"Bad"}]}]}]}}
        """);
        Assert.True(searched.SearchVerified); Assert.Single(searched.Sources);
    }

    [Fact]
    public async Task StreamedSearchEvidenceSurvivesEmptyFinalOutput()
    {
        const string events = """
        data: {"type":"response.web_search_call.completed"}
        data: {"type":"response.output_item.done","item":{"type":"web_search_call","status":"completed","action":{"sources":[{"url":"https://example.com/news","title":"News"},{"url":"https://user:password@example.com/private"}]}}}
        data: {"type":"response.output_text.delta","delta":"Today's news"}
        data: {"type":"response.output_text.annotation.added","annotation":{"type":"url_citation","url":"https://example.com/news","title":"News"}}
        """;
        var result = await Parse(events + "\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n");
        Assert.True(result.SearchVerified);
        Assert.Equal("Today's news", result.Answer);
        Assert.Single(result.Sources);
        await Assert.ThrowsAsync<ChatGptConnectionException>(() => Parse(events));
        await Assert.ThrowsAsync<ChatGptConnectionException>(() => Parse(events + "\ndata: {\"type\":\"response.failed\"}\n"));
    }

    [Fact]
    public async Task SearchStartedOrCitationAloneDoesNotProveSearchCompleted()
    {
        var result = await Parse("""
        data: {"type":"response.web_search_call.searching"}
        data: {"type":"response.output_text.annotation.added","annotation":{"type":"url_citation","url":"https://example.com","title":"News"}}
        data: {"type":"response.output_text.delta","delta":"Unverified"}
        data: {"type":"response.completed","response":{"status":"completed","output":[]}}
        """);
        Assert.False(result.SearchVerified);
    }

    private static Task<ChatGptTestResult> Parse(string s) => ChatGptSubscriptionClient.ParseResponseAsync(new MemoryStream(Encoding.UTF8.GetBytes(s)), default);
    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "bantera-device-" + Guid.NewGuid());
        public Guid Admin { get; } = Guid.NewGuid(); public string Binding { get; } = ChatGptConnection.RandomValue();
        public Provider Provider { get; } = new(); public HttpClient Http { get; }
        public ChatGptConnectionOptions Config { get; } public ChatGptConnection Service { get; }
        public Fixture() {
            Config = new() { StorageDirectory = Directory, EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            Http = new(Provider); Service = new(Http, Options.Create(Config));
        }
        public Task<ChatGptDeviceStart> Start() => Service.StartDeviceAsync(Admin, Binding, default);
        public Task<string> Poll(ChatGptDeviceStart start) => Service.PollDeviceAsync(Admin, Binding, start.Attempt, default);
        public void ReadyToPoll(bool expired = false) {
            var purpose = "device-pending-" + Admin.ToString("N"); var path = Path.Combine(Directory, purpose + ".enc");
            var key = Convert.FromBase64String(Config.EncryptionKey);
            var pending = JsonSerializer.Deserialize<ChatGptDevicePending>(ChatGptConnection.Unseal(File.ReadAllBytes(path), purpose, key))!;
            pending = pending with { NextPollAt = DateTimeOffset.UtcNow.AddSeconds(-1), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 5) };
            File.WriteAllText(path, Convert.ToBase64String(ChatGptConnection.Seal(JsonSerializer.SerializeToUtf8Bytes(pending), purpose, key)));
        }
        public void Dispose() { Http.Dispose(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
    private sealed class Provider : HttpMessageHandler
    {
        public int Polls; public int Refreshes; public bool ShortExpiry; public HttpStatusCode PollStatus = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            object body;
            if (request.RequestUri!.AbsolutePath.EndsWith("/usercode")) body = new { device_auth_id = "secret-device-id", user_code = "ABCD-EFGH", interval = "5" };
            else if (request.RequestUri.AbsolutePath.EndsWith("deviceauth/token")) {
                Polls++; if (PollStatus != HttpStatusCode.OK) return new(PollStatus);
                body = new { authorization_code = "private-code", code_verifier = "private-verifier" };
            } else {
                var form = await request.Content!.ReadAsStringAsync(ct); var refresh = form.Contains("grant_type=refresh_token");
                if (refresh) Refreshes++;
                else { Assert.Contains("code_verifier=private-verifier", form); Assert.Contains("client_id=" + ChatGptConnection.CodexClientId, form); }
                var jwt = new JwtSecurityToken(claims: [new Claim("email", "test@example.com"), new Claim("https://api.openai.com/auth", "{\"chatgpt_account_id\":\"account-123\"}", "JSON")], expires: DateTime.UtcNow.AddHours(1));
                body = new { access_token = new JwtSecurityTokenHandler().WriteToken(jwt), refresh_token = refresh ? "rotated-refresh" : "private-refresh",
                    id_token = new JwtSecurityTokenHandler().WriteToken(jwt), expires_in = refresh || !ShortExpiry ? 3600 : 30 };
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
}

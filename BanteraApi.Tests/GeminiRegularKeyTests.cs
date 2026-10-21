using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using BanteraApi.Admin;
using BanteraApi.Database;
using BanteraApi.Gemini;
using BanteraApi.OpenAi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class GeminiRegularKeyTests
{
    private const string EnvironmentKey = "AIzaSy-environment-test-key";
    private const string SavedKey = "AIzaSy-dashboard-test-key";
    private const string PaidKey = "AIzaSy-paid-fallback-test-key";
    private static string Id(string key) => GeminiKeyHealthService.KeyId(key);

    [Fact]
    public void EncryptedRoundTripDoesNotExposeKeysOrRemarksAndRejectsWrongSecret()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        RegularKeyEntry[] entries = [new(Id(SavedKey), SavedKey, "project notes"), new(Id(EnvironmentKey), null, "environment notes", false)];
        var encoded = GeminiRegularKeyStore.Encode(entries, secret);
        Assert.DoesNotContain(SavedKey, encoded);
        Assert.DoesNotContain("project notes", encoded);
        Assert.Equal(entries, GeminiRegularKeyStore.Decode(encoded, secret));
        Assert.ThrowsAny<CryptographicException>(() => GeminiRegularKeyStore.Decode(encoded, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
    }

    [Fact]
    public void MergeDeduplicatesAndRespectsEnvironmentAndSavedDisables()
    {
        RegularKeyEntry[] entries = [new(Id(SavedKey), SavedKey, "saved"), new(Id(EnvironmentKey), null, "env", false)];
        Assert.Equal([SavedKey], GeminiRegularKeyStore.Merge([EnvironmentKey, SavedKey, " "], entries));
        Assert.Equal([EnvironmentKey, SavedKey], GeminiRegularKeyStore.Merge([EnvironmentKey, SavedKey], entries, true));
        Assert.Empty(GeminiRegularKeyStore.Merge([EnvironmentKey], entries.Select(e => e with { Enabled = false }).ToArray()));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("short", false)]
    [InlineData("AIzaSy-invalid key with spaces", false)]
    [InlineData("AIzaSy-valid-key-for-testing", true)]
    [InlineData("AQ.valid-key-for-testing", true)]
    public void ValidatesKeyFormat(string? key, bool expected) => Assert.Equal(expected, GeminiRegularKeyEndpoints.ValidKey(key));

    [Fact]
    public void BoundsKeysAndRemarks()
    {
        Assert.False(GeminiRegularKeyEndpoints.ValidKey(new string('a', 4097)));
        Assert.True(GeminiRegularKeyEndpoints.ValidRemarks(new string('a', 1000)));
        Assert.False(GeminiRegularKeyEndpoints.ValidRemarks(new string('a', 1001)));
    }

    [Fact]
    public async Task SavedKeysParticipateInAllPlansCooldownsAndPaidAccessRules()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new GeminiSettings { ApiKeys = [EnvironmentKey] });
        var user = Guid.NewGuid();
        var paid = new FakePaid(scopes, new(PaidKey, [user], PracticeAudioFallback: true, DialogueAudioFallback: true));
        var regular = new FakeRegular(scopes, options, paid) { Keys = [EnvironmentKey, SavedKey, PaidKey, SavedKey, "AQ.another-regular-key"] };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var health = new GeminiKeyHealthService(scopes, cache, options, NullLogger<GeminiKeyHealthService>.Instance, paid, regular);
        var anonymous = await health.PlanAsync("text", null, default);
        Assert.Equal(3, anonymous.Keys.Length);
        Assert.Contains(SavedKey, anonymous.Keys);
        Assert.DoesNotContain(PaidKey, anonymous.Keys);
        foreach (var (practice, dialogue) in new[] { (true, false), (false, true), (false, false) }) {
            var plan = await health.PlanAsync("tts", user, default, practiceAudio: practice, dialogueAudio: dialogue);
            Assert.Equal(PaidKey, plan.Keys.Last());
            Assert.Contains(SavedKey, plan.Keys);
        }
        var search = await health.PlanAsync("search", null, default, webSearch: true);
        Assert.Equal(2, search.Keys.Length);
        Assert.All(search.Keys, key => Assert.StartsWith("AIzaSy", key));
        health.CoolDown(SavedKey, "tts");
        Assert.DoesNotContain(SavedKey, (await health.PlanAsync("tts", null, default)).Keys);
        Assert.Contains(SavedKey, (await health.PlanAsync("text", null, default)).Keys);
        Assert.Contains((await health.GetSnapshotAsync(default)).Items, i => i.Id == Id(SavedKey) && i.Status == "cooldown");
        regular.Keys = [EnvironmentKey];
        Assert.DoesNotContain(SavedKey, (await health.PlanAsync("text", null, default)).Keys);
    }

    [RegularKeysDatabaseFact]
    public async Task AdminApiPersistsEncryptedKeysRemarksAndConcurrentEditsWithAuthorization()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_REGULAR_KEYS_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("Database=bantera_regular_keys_verify", connection);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        builder.Services.Configure<GeminiSettings>(o => o.ApiKeys = [EnvironmentKey]);
        builder.Services.Configure<ChatGptConnectionOptions>(o => o.EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.Services.AddSingleton<GeminiPaidFallback>();
        builder.Services.AddSingleton<GeminiRegularKeyStore>();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("admin")));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        GeminiRegularKeyEndpoints.Map(app);
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            await db.AppSettings.Where(s => s.Key == GeminiRegularKeyStore.Setting || s.Key == GeminiPaidFallback.Setting).ExecuteDeleteAsync();
        }
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        const string path = "/api/admin/ai-settings/regular-keys";
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete }) {
            var target = method == HttpMethod.Put || method == HttpMethod.Delete ? path + "/" + Id(SavedKey) : path;
            using var request = new HttpRequestMessage(method, target);
            if (method == HttpMethod.Post || method == HttpMethod.Put) request.Content = JsonContent.Create(new { apiKey = SavedKey, remarks = "" });
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
            using var nonAdmin = new HttpRequestMessage(method, target);
            nonAdmin.Headers.Add("X-Test-Role", "user");
            if (method == HttpMethod.Post || method == HttpMethod.Put) nonAdmin.Content = JsonContent.Create(new { apiKey = SavedKey, remarks = "" });
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(nonAdmin)).StatusCode);
        }
        client.DefaultRequestHeaders.Add("X-Test-Role", "admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { apiKey = "bad" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(path, new { apiKey = SavedKey, remarks = "Project One" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { apiKey = SavedKey })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { apiKey = EnvironmentKey })).StatusCode);
        var response = await client.GetAsync(path);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SavedKey, json); Assert.DoesNotContain(EnvironmentKey, json);
        var list = await response.Content.ReadFromJsonAsync<RegularKeyStatus>();
        Assert.Equal(2, list!.Items.Length);
        Assert.Equal("Project One", list.Items.Single(i => i.Id == Id(SavedKey)).Remarks);
        var store = app.Services.GetRequiredService<GeminiRegularKeyStore>();
        var admin = Guid.NewGuid();
        // Separate concurrent edits must not overwrite each other.
        await Task.WhenAll(store.UpdateAsync(Id(EnvironmentKey), "Env notes", true, admin, default),
            store.UpdateAsync(Id(SavedKey), "Saved notes", false, admin, default));
        var status = await store.StatusAsync(default);
        Assert.Equal("Env notes", status.Items.Single(i => i.Id == Id(EnvironmentKey)).Remarks);
        Assert.Equal("Saved notes", status.Items.Single(i => i.Id == Id(SavedKey)).Remarks);
        Assert.Equal([EnvironmentKey], await store.KeysAsync(default));
        // A fresh service reads the same encrypted keys after a restart.
        var fresh = new GeminiRegularKeyStore(app.Services.GetRequiredService<IServiceScopeFactory>(),
            app.Services.GetRequiredService<IOptions<GeminiSettings>>(), app.Services.GetRequiredService<IOptions<ChatGptConnectionOptions>>(),
            app.Services.GetRequiredService<GeminiPaidFallback>());
        Assert.Equal(2, (await fresh.StatusAsync(default)).Items.Length);
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var value = await db.AppSettings.Where(s => s.Key == GeminiRegularKeyStore.Setting).Select(s => s.Value).SingleAsync();
            Assert.DoesNotContain(SavedKey, value); Assert.DoesNotContain("Saved notes", value);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path + "/" + Id(SavedKey), new { remarks = "Updated", enabled = true })).StatusCode);
        Assert.Contains(SavedKey, await fresh.KeysAsync(default));
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(path + "/" + Id(EnvironmentKey))).StatusCode);
        await app.Services.GetRequiredService<GeminiPaidFallback>().SaveAsync(PaidKey, false, [], [], admin, default);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { apiKey = PaidKey })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path + "/" + Id(SavedKey))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(path + "/" + Id(SavedKey))).StatusCode);
        Assert.Equal([EnvironmentKey], await fresh.KeysAsync(default));
        await store.UpdateAsync(Id(EnvironmentKey), "Old environment notes", false, admin, default);
        app.Services.GetRequiredService<IOptions<GeminiSettings>>().Value.ApiKeys = [];
        await store.AddAsync(EnvironmentKey, "Moved to dashboard", admin, default);
        var moved = Assert.Single((await store.StatusAsync(default)).Items);
        Assert.True(moved.Enabled);
        Assert.Equal("dashboard", moved.Source);
        Assert.Equal("Moved to dashboard", moved.Remarks);
        Assert.Equal([EnvironmentKey], await store.KeysAsync(default));
        await app.StopAsync();
    }

    private sealed class FakePaid(IServiceScopeFactory scopes, PaidFallbackPolicy policy)
        : GeminiPaidFallback(scopes, Options.Create(new ChatGptConnectionOptions()))
    {
        public override Task<PaidFallbackPolicy> ReadAsync(CancellationToken ct) => Task.FromResult(policy);
        public override Task<PaidFallbackPolicy> ForUserAsync(Guid? user, CancellationToken ct) => Task.FromResult(policy);
    }
    private sealed class FakeRegular(IServiceScopeFactory scopes, IOptions<GeminiSettings> options, GeminiPaidFallback paid)
        : GeminiRegularKeyStore(scopes, options, Options.Create(new ChatGptConnectionOptions()), paid)
    {
        public string[] Keys = [];
        public override Task<string[]> KeysAsync(CancellationToken ct, bool includeDisabled = false) => Task.FromResult(Keys);
    }
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (role.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", "a321b323-057f-45c7-87cb-2e37c09db4fe"), new Claim(ClaimTypes.Role, role)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
public sealed class RegularKeysDatabaseFactAttribute : FactAttribute
{
    public RegularKeysDatabaseFactAttribute() {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BANTERA_REGULAR_KEYS_TEST_DB")))
            Skip = "Set BANTERA_REGULAR_KEYS_TEST_DB to disposable local bantera_regular_keys_verify.";
    }
}

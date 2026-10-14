using System.Net;
using System.Security.Cryptography;
using BanteraApi.Chat.Ai;
using BanteraApi.Gemini;
using BanteraApi.OpenAi;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using BanteraApi.Admin;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Tests;

public class GeminiPaidFallbackTests
{
    [Theory]
    [InlineData("AIzaSy", 33)]
    [InlineData("AQ.", 250)]
    [InlineData("AQ.", 3000)]
    public void SaveAcceptsLegacyAndLongAuthorizationKeys(string prefix, int length)
    {
        Assert.Null(GeminiPaidFallbackEndpoints.Validate(new(prefix + new string('a', length), false, [Allowed], [Allowed])));
    }
    [Theory]
    [InlineData("AQ.abcdefghijklmnopqr\nstuv", "invalid_key_format")]
    [InlineData("\"AIzaSyabcdefghijklmnopqrst\"", "invalid_key_format")]
    [InlineData("too-short", "invalid_key_format")]
    public void SaveRejectsMalformedKeysWithSpecificSafeError(string key, string code)
    {
        var error = GeminiPaidFallbackEndpoints.Validate(new(key, false, [Allowed]));
        Assert.Equal(code, error?.Code);
        Assert.DoesNotContain(key, error!.Message);
    }
    [PaidSettingsDatabaseFact]
    public async Task LongAuthKeyPersistsEncryptedAndBlankSavePreservesIt()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_PAID_SETTINGS_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("Database=bantera_paid_settings_verify", connection);
        await using var services = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseNpgsql(connection)).BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var store = new GeminiPaidFallback(scopes, Options.Create(new ChatGptConnectionOptions { EncryptionKey = secret }));
        var user = Guid.NewGuid();
        using (var scope = scopes.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            // Each run uses a new encryption key in this explicitly disposable database.
            await db.AppSettings.Where(s => s.Key == GeminiPaidFallback.Setting).ExecuteDeleteAsync();
            db.Users.Add(new User { Id = user, Name = "Key save test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var key = "AQ." + new string('a', 3000);
        await store.SaveAsync(key, false, [user], [user], user, default);
        var status = await store.StatusAsync(default);
        Assert.True(status.Configured);
        Assert.Equal("paid_only", Assert.Single(status.Users).Mode);
        await store.SaveAsync(null, false, [user], [user], user, default);
        Assert.Equal(key, (await store.ReadAsync(default)).Key);
        using (var scope = scopes.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.AppSettings.SingleAsync(s => s.Key == GeminiPaidFallback.Setting);
            Assert.True(row.Value.Length > 2000);
            Assert.DoesNotContain(key, row.Value);
        }
        // App preferences retain permission while changing only this user's mode.
        Assert.True((await store.PreferenceAsync(user, default)).CanConfigure);
        foreach (var mode in new[] { "regular_only", "fallback", "paid_only" }) {
            Assert.True(await store.SavePreferenceAsync(user, mode, default));
            var preference = await store.PreferenceAsync(user, default);
            Assert.True(preference.CanConfigure);
            Assert.Equal(mode, preference.Mode);
            Assert.Equal(key, (await store.ReadAsync(default)).Key);
        }
        Assert.False(await store.SavePreferenceAsync(Guid.NewGuid(), "paid_only", default));
        Assert.False((await store.PreferenceAsync(Guid.NewGuid(), default)).CanConfigure);
        await store.SavePreferenceAsync(user, "regular_only", default);
        Assert.Empty((await store.ForUserAsync(user, default)).AllowedUserIds);
        // Legacy admin payloads preserve a user's regular-only preference.
        await store.SaveAsync(null, false, [user], [], user, default);
        Assert.Equal("regular_only", (await store.PreferenceAsync(user, default)).Mode);
        await store.SaveAsync(null, false, [], [], user, default);
        Assert.False(await store.SavePreferenceAsync(user, "paid_only", default));
        Assert.False((await store.PreferenceAsync(user, default)).CanConfigure);
        await store.SaveAsync(null, true, [], [], user, default);
        Assert.False((await store.StatusAsync(default)).Configured);
    }
    private static readonly Guid Allowed = Guid.NewGuid();
    private const string Paid = "AIzaSy-paid-secret-testing";
    private static readonly PaidFallbackPolicy Policy = new(Paid, [Allowed]);

    [Fact]
    public void DefaultDeniedAndNoIdentityNeverReceivePaidEvenIfDuplicatedInConfig()
    {
        foreach (var user in new Guid?[] { null, Guid.NewGuid() })
            Assert.Equal(["a", "b"], GeminiPaidFallback.Order([Paid, "a", "b", Paid], Policy, user));
    }
    [Fact]
    public void PaidLastEvenIfResumePrefersPaidAndRegularKeysAreDeduplicated()
    {
        Assert.Equal(["a", "b", Paid], GeminiPaidFallback.Order([Paid, "a", "a", "b"], Policy, Allowed, AiLiveResumeCache.Hash(Paid)));
        Assert.Equal(["b", "a", Paid], GeminiPaidFallback.Order(["a", "b"], Policy, Allowed, AiLiveResumeCache.Hash("b")));
    }
    [Fact]
    public void PaidOnlyIsExplicitAndDoesNotFallBackToRegularKeys()
    {
        var policy = Policy with { PaidOnlyUserIds = [Allowed] };
        Assert.Equal([Paid], GeminiPaidFallback.Order(["a", "b"], policy, Allowed));
        Assert.Empty(GeminiPaidFallback.Order(["a", "b"], policy with { Key = null }, Allowed));
        Assert.Equal(["a", "b"], GeminiPaidFallback.Order([Paid, "a", "b"], policy, Guid.NewGuid()));
    }
    [Fact]
    public void RegularOnlyKeepsPermissionButExcludesPaidKeyEvenWhenDuplicated()
    {
        var policy = Policy with { RegularOnlyUserIds = [Allowed] };
        Assert.Equal("regular_only", GeminiPaidFallback.Mode(policy, Allowed));
        Assert.Contains(Allowed, policy.AllowedUserIds);
        Assert.Equal(["a", "b"], GeminiPaidFallback.Order([Paid, "a", "b"], policy, Allowed));
        Assert.NotNull(GeminiPaidFallbackEndpoints.Validate(new(null, false, [Allowed], [Allowed], [Allowed])));
    }
    [Fact]
    public void SearchStillRequiresApprovedKeyPrefix()
    {
        Assert.Equal(["a"], GeminiPaidFallback.Order(["a"], Policy with { Key = "not-a-search-key" }, Allowed, webSearch: true));
    }
    [Fact]
    public void EncryptedSettingRoundTripsAndRejectsTampering()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var policy = Policy with { PaidOnlyUserIds = [Allowed] };
        var encoded = GeminiPaidFallback.Encode(policy, secret);
        Assert.DoesNotContain(Paid, encoded);
        var decoded = GeminiPaidFallback.Decode(encoded, secret);
        Assert.Equal(Paid, decoded.Key);
        Assert.Equal([Allowed], decoded.AllowedUserIds);
        Assert.Equal([Allowed], decoded.PaidOnlyUserIds);
        Assert.ThrowsAny<CryptographicException>(() => GeminiPaidFallback.Decode(encoded, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
    }
    [Fact]
    public async Task HealthFilteringAndRevocationApplyBeforePaidAttempt()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scope = provider.GetRequiredService<IServiceScopeFactory>();
        var store = new Store(scope, Policy);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var health = new GeminiKeyHealthService(scope, cache, Options.Create(new GeminiSettings { ApiKeys = [Paid, "a", "b"] }), NullLogger<GeminiKeyHealthService>.Instance, store);
        health.CoolDown("a", "live"); health.CoolDown("b", "live");
        Assert.Equal([Paid], (await health.PlanAsync("live", Allowed, default)).Keys);
        Assert.Empty((await health.PlanAsync("live", Guid.NewGuid(), default)).Keys);
        Assert.True(await health.StillAllowedAsync(Paid, Allowed, default));
        store.Policy = Policy with { AllowedUserIds = [] };
        Assert.False(await health.StillAllowedAsync(Paid, Allowed, default));
        store.Policy = Policy;
        health.CoolDown(Paid, "live");
        Assert.Empty((await health.PlanAsync("live", Allowed, default)).Keys);
    }
    [Fact]
    public async Task LiveTriesEveryRegularKeyBeforePaidAndNeverPaidAfterRegularSuccess()
    {
        foreach (var regularSucceeds in new[] { false, true }) {
            var seen = new List<string>();
            var result = await GeminiLiveService.TryKeysAsync(["a", "b", Paid], (key, _) => {
                seen.Add(key);
                if (key == Paid || regularSucceeds && key == "b") return Task.FromResult(key);
                throw new HttpRequestException("quota", null, HttpStatusCode.TooManyRequests);
            }, _ => { }, default, retryProviderFailures: true);
            Assert.Equal(regularSucceeds ? "b" : Paid, result);
            Assert.Equal(regularSucceeds ? new[] { "a", "b" } : new[] { "a", "b", Paid }, seen);
        }
    }
    [Fact]
    public async Task LiveOperationalFailuresReachPaidButCancellationAndPartialAudioDoNot()
    {
        var seen = new List<string>();
        await GeminiLiveService.TryKeysAsync(["a", "b", Paid], (key, _) => {
            seen.Add(key);
            if (key == "a") throw new HttpRequestException("invalid", null, HttpStatusCode.Unauthorized);
            if (key == "b") throw new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable);
            return Task.FromResult(true);
        }, _ => Assert.Fail("Not quota"), default, true);
        Assert.Equal(["a", "b", Paid], seen);
        seen.Clear();
        await Assert.ThrowsAsync<HttpRequestException>(() => GeminiLiveService.TryKeysAsync<bool>(["a", Paid], (key, _) => {
            seen.Add(key); var error = new HttpRequestException("failed", null, HttpStatusCode.ServiceUnavailable); error.Data["partialAudio"] = true; throw error;
        }, _ => { }, default, true));
        Assert.Equal(["a"], seen);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GeminiLiveService.TryKeysAsync([Paid], (_, _) => Task.FromResult(true), _ => { }, cancelled.Token, true));
    }
    [Fact]
    public async Task DiagnosticUserContextDoesNotLeakAcrossRequests()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var recorder = new BanteraApi.Diagnostics.AiPipelineEventRecorder(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<BanteraApi.Diagnostics.AiPipelineEventRecorder>.Instance);
        Assert.Null(recorder.CurrentUserId);
        using (recorder.BeginContext(Allowed, null, null, "test")) {
            await Task.Yield(); Assert.Equal(Allowed, recorder.CurrentUserId);
            using (recorder.BeginContext(null, null, null, "anonymous")) Assert.Null(recorder.CurrentUserId);
            Assert.Equal(Allowed, recorder.CurrentUserId);
        }
        Assert.Null(recorder.CurrentUserId);
    }
    private sealed class Store(IServiceScopeFactory scopes, PaidFallbackPolicy policy)
        : GeminiPaidFallback(scopes, Options.Create(new ChatGptConnectionOptions()))
    {
        public PaidFallbackPolicy Policy = policy;
        public override Task<PaidFallbackPolicy> ReadAsync(CancellationToken ct) => Task.FromResult(Policy);
        public override Task<PaidFallbackPolicy> ForUserAsync(Guid? user, CancellationToken ct) => Task.FromResult(Policy);
    }
}

public sealed class PaidSettingsDatabaseFactAttribute : FactAttribute
{
    public PaidSettingsDatabaseFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BANTERA_PAID_SETTINGS_TEST_DB")))
            Skip = "Set BANTERA_PAID_SETTINGS_TEST_DB to a disposable local bantera_paid_settings_verify database.";
    }
}

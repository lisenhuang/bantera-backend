using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Profile;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public sealed class WordActivityTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);
    [Fact]
    public void ValidatesLocalDateAndBoundedNonnegativeSnapshots()
    {
        var good = new WordActivitySnapshot(Guid.NewGuid(), [new(Today, 20, 10)]);
        Assert.True(WordActivityEndpoints.IsValid(good, Today));
        Assert.True(WordActivityEndpoints.IsValid(good with { Days = [new(Today.AddDays(1), 0, 0)] }, Today));
        Assert.False(WordActivityEndpoints.IsValid((WordActivitySnapshot?)null, Today));
        Assert.False(WordActivityEndpoints.IsValid(good with { DeviceId = Guid.Empty }, Today));
        Assert.False(WordActivityEndpoints.IsValid(good with { Days = [] }, Today));
        Assert.False(WordActivityEndpoints.IsValid(good with { Days = [new(Today, -1, 0)] }, Today));
        Assert.False(WordActivityEndpoints.IsValid(good with { Days = [new(Today, 0, 10_000_001)] }, Today));
        Assert.False(WordActivityEndpoints.IsValid(good with { Days = [new(Today.AddDays(2), 1, 0)] }, Today));
        Assert.False(WordActivityEndpoints.IsValid(good with { Days = [new(Today, 1, 0), new(Today, 2, 0)] }, Today));
        Assert.False(WordActivityEndpoints.IsValid(good with { Days = Enumerable.Range(0, 32).Select(i => new WordActivityDay(Today.AddDays(-i), 1, 1)).ToArray() }, Today));
    }

    [Theory]
    [InlineData("en-NZ", "en")]
    [InlineData("en_US", "en")]
    [InlineData("fr-CA", "fr")]
    [InlineData("zh-TW", "zh")]
    [InlineData("zh-Hant-HK", "yue")]
    [InlineData("yue-CN", "yue")]
    [InlineData("iw-IL", "he")]
    public void LanguageKeysIgnoreAccent(string locale, string expected) =>
        Assert.Equal(expected, WordActivityEndpoints.NormalizeLanguage(locale));

    [Fact]
    public void LanguageSnapshotsValidateEachLanguageAndMergeAccentIdentity()
    {
        var good = new LanguageWordActivitySnapshot(Guid.NewGuid(), [new(Today, "en-NZ", 10, 5), new(Today, "ja-JP", 20, 7)]);
        Assert.True(WordActivityEndpoints.IsValid(good, Today));
        Assert.True(WordActivityEndpoints.IsValid(good with { Days = [new(Today, "", 10, 5)] }, Today));
        Assert.False(WordActivityEndpoints.IsValid(good with { Days = [new(Today, "en-NZ", 10, 5), new(Today, "en-US", 20, 7)] }, Today));
        Assert.False(WordActivityEndpoints.IsValid(good with { Days = [new(Today, "../en", 10, 5)] }, Today));
        Assert.False(WordActivityEndpoints.IsValid(good with { Days = [new(Today, null!, 10, 5)] }, Today));
        Assert.False(WordActivityEndpoints.IsValid(good with { Days = [new(Today, "en", -1, 5)] }, Today));
    }

    [WordActivityDatabaseFact]
    public async Task AuthenticatedSnapshotsAreIdempotentIsolatedAndSurviveDeviceRetries()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_WORD_ACTIVITY_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("bantera_word_activity_verify", connection);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        WordActivityEndpoints.Map(app);
        var userId = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var snapshot = new WordActivitySnapshot(Guid.NewGuid(), [new(today, 40, 12)]);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Verify the additive migration against an actual legacy row.
            await db.GetService<IMigrator>().MigrateAsync("20260929235949_AddUserWordActivity");
            db.Users.AddRange(new User { Id = userId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new User { Id = otherUser, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            db.UserWordActivities.Add(new UserWordActivity { UserId = userId, DeviceId = snapshot.DeviceId,
                Date = today, ListenedWords = 40, SpokenWords = 12 });
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
            Assert.Equal(40, (await db.UserWordActivities.SingleAsync(x => x.UserId == userId)).ListenedWords);
        }
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me/word-stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/me/word-stats", snapshot)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v2/me/word-stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v2/me/word-stats",
            new LanguageWordActivitySnapshot(snapshot.DeviceId, [new(today, "en", 1, 1)]))).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        // Same payload including concurrent retries must contribute once.
        var retries = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostAsJsonAsync("/api/me/word-stats", snapshot)));
        Assert.All(retries, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/me/word-stats", snapshot with { Days = [new(today, 25, 8)] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/me/word-stats", snapshot with { DeviceId = Guid.NewGuid(), Days = [new(today, 10, 3)] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/me/word-stats", snapshot with { Days = [new(today, -1, 0)] })).StatusCode);
        var report = await client.GetFromJsonAsync<Report>("/api/me/word-stats");
        var day = Assert.Single(report!.Days);
        Assert.Equal(50, day.ListenedWords);
        Assert.Equal(15, day.SpokenWords);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v2/me/word-stats",
            new LanguageWordActivitySnapshot(snapshot.DeviceId, [new(today, "", 40, 12), new(today, "en-NZ", 80, 30), new(today, "ja-JP", 25, 18)]))).StatusCode);
        var languageRetry = new LanguageWordActivitySnapshot(snapshot.DeviceId, [new(today, "en-US", 70, 25)]);
        var languageRetries = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostAsJsonAsync("/api/v2/me/word-stats", languageRetry)));
        Assert.All(languageRetries, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var languageReport = await client.GetFromJsonAsync<LanguageReport>("/api/v2/me/word-stats");
        Assert.Equal(3, languageReport!.Days.Length);
        Assert.Equal(50, Assert.Single(languageReport.Days, x => x.Language == "").ListenedWords);
        Assert.Equal(80, Assert.Single(languageReport.Days, x => x.Language == "en").ListenedWords);
        Assert.Equal(18, Assert.Single(languageReport.Days, x => x.Language == "ja").SpokenWords);
        var compatibleReport = await client.GetFromJsonAsync<Report>("/api/me/word-stats");
        Assert.Equal(155, Assert.Single(compatibleReport!.Days).ListenedWords);
        Assert.Equal(63, Assert.Single(compatibleReport.Days).SpokenWords);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", otherUser.ToString());
        Assert.Empty((await client.GetFromJsonAsync<Report>("/api/me/word-stats"))!.Days);
        Assert.Empty((await client.GetFromJsonAsync<LanguageReport>("/api/v2/me/word-stats"))!.Days);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(x => x.Id == userId || x.Id == otherUser).ExecuteDeleteAsync();
            Assert.False(await db.UserWordActivities.AnyAsync(x => x.UserId == userId));
            Assert.False(await db.Set<UserLanguageWordActivity>().AnyAsync(x => x.UserId == userId));
        }
        await app.StopAsync();
    }

    private sealed record LanguageReport(LanguageWordActivityDay[] Days);
    private sealed record Report(WordActivityDay[] Days);
    public sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].ToString();
            if (!Guid.TryParse(user, out _)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "test")));
        }
    }
}

public sealed class WordActivityDatabaseFactAttribute : FactAttribute
{
    public WordActivityDatabaseFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BANTERA_WORD_ACTIVITY_TEST_DB")))
            Skip = "Set BANTERA_WORD_ACTIVITY_TEST_DB to a disposable local bantera_word_activity_verify database.";
    }
}

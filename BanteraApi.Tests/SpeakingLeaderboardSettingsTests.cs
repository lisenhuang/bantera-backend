using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BanteraApi.Auth;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Leaderboards;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class SpeakingLeaderboardSettingsTests
{
    [Theory]
    [InlineData(null, 30)]
    [InlineData("", 30)]
    [InlineData("wrong", 30)]
    [InlineData("1.5", 30)]
    [InlineData("0", 30)]
    [InlineData("366", 30)]
    [InlineData("1", 1)]
    [InlineData("14", 14)]
    [InlineData("365", 365)]
    public void MissingOrInvalidSavedPeriodDefaultsToThirty(string? value, int days) =>
        Assert.Equal(days, SpeakingLeaderboardSettings.Resolve(value));

    [Theory]
    [InlineData(1, 35)]
    [InlineData(30, 35)]
    [InlineData(31, 36)]
    [InlineData(90, 95)]
    [InlineData(365, 370)]
    public void RetentionPreservesTheDefaultAndExtendsOnlyForConfiguredDays(int days, int retention) =>
        Assert.Equal(retention, SpeakingLeaderboardSettings.RetentionDays(days));

    [AiDatabaseFact]
    public async Task ConfiguredWindowPersistsUsesExactPeriodAndLeavesLegacyRoutesUnchanged()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("Database=bantera_ai_verify_", connection);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddScoped<SpeakingLeaderboardService>();
        builder.Services.AddSingleton<IOptions<JwtSettings>>(Options.Create(new JwtSettings { Secret = "isolated-configured-ranking-secret-long-enough" }));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, UserAuth>("test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy("Admin", policy => policy.RequireRole("admin")));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); SpeakingLeaderboardEndpoints.Map(app);
        var admin = Guid.NewGuid(); var learner = Guid.NewGuid(); var other = Guid.NewGuid();
        var now = DateTime.UtcNow.AddSeconds(-5);
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            await db.AppSettings.Where(s => s.Key == SpeakingLeaderboardSettings.Key).ExecuteDeleteAsync();
            db.Users.AddRange(new User { Id = admin, Role = "admin", Status = "active" },
                new User { Id = learner, Name = "Configured learner", Status = "active" },
                new User { Id = other, Name = "Other configured learner", Status = "active" });
            foreach (var (when, count) in new[] { (now.AddHours(-12), 100), (now.AddDays(-20), 200), (now.AddDays(-31), 500),
                (now.AddDays(-14), 1000), (now.AddDays(-14).AddMilliseconds(1), 10) })
                db.Add(new SpeakingEvent { UserId = learner, DeviceId = learner, EventId = Guid.NewGuid(), OccurredAt = when, Language = "en", SpokenWords = count });
            db.Add(new SpeakingEvent { UserId = other, DeviceId = other, EventId = Guid.NewGuid(), OccurredAt = now.AddDays(-10), Language = "ja", SpokenWords = 300 });
            await db.SaveChangesAsync();
        }
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        void Login(Guid id, string role) {
            client.DefaultRequestHeaders.Remove("X-Test-User"); client.DefaultRequestHeaders.Add("X-Test-User", id.ToString());
            client.DefaultRequestHeaders.Remove("X-Test-Role"); client.DefaultRequestHeaders.Add("X-Test-Role", role);
        }
        async Task<JsonElement> Get(string path) {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        var cutoff = Uri.EscapeDataString(now.ToString("O"));
        try {
            Login(admin, "admin");
            Assert.Equal(30, (await Get("/api/admin/speaking-leaderboard-settings")).GetProperty("days").GetInt32());
            Login(learner, "user");
            var report = await Get("/api/speaking-leaderboard/configured?until=" + cutoff);
            Assert.Equal(30, report.GetProperty("periodDays").GetInt32());
            Assert.Equal(1310, report.GetProperty("you").GetProperty("spokenWords").GetInt64());
            Assert.False(report.GetProperty("canChoosePeriod").GetBoolean());
            Assert.Equal(now.AddDays(-30), report.GetProperty("fromUtc").GetDateTime());
            Assert.Equal(now, report.GetProperty("throughUtc").GetDateTime());
            Login(admin, "admin");
            using var saved = await client.PutAsJsonAsync("/api/admin/speaking-leaderboard-settings", new { days = 14 });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(14, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("days").GetInt32());
            Assert.Equal(14, (await Get("/api/admin/speaking-leaderboard-settings")).GetProperty("days").GetInt32());
            using (var scope = app.Services.CreateScope()) {
                var setting = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AppSettings.SingleAsync(s => s.Key == SpeakingLeaderboardSettings.Key);
                Assert.Equal("14", setting.Value); Assert.Equal(admin, setting.UpdatedByUserId);
            }
            Login(learner, "user");
            report = await Get("/api/speaking-leaderboard/configured?until=" + cutoff);
            Assert.Equal(14, report.GetProperty("periodDays").GetInt32());
            Assert.Equal(110, report.GetProperty("you").GetProperty("spokenWords").GetInt64());
            Assert.Equal(now.AddDays(-14), report.GetProperty("fromUtc").GetDateTime());
            Assert.Equal(14, (await Get("/api/speaking-leaderboard/configured?offset=100&until=" + cutoff)).GetProperty("periodDays").GetInt32());
            var legacy = await Get("/api/speaking-leaderboard?hours=720&until=" + cutoff);
            Assert.Equal(30, legacy.GetProperty("periodDays").GetInt32());
            Assert.Equal(1310, legacy.GetProperty("you").GetProperty("spokenWords").GetInt64());
            Assert.Equal(7, (await Get("/api/speaking-leaderboard")).GetProperty("periodDays").GetInt32());
            foreach (var days in new[] { 1, 365 }) {
                Login(admin, "admin");
                Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/admin/speaking-leaderboard-settings", new { days })).StatusCode);
                Login(learner, "user");
                report = await Get("/api/speaking-leaderboard/configured?until=" + cutoff);
                Assert.Equal(days, report.GetProperty("periodDays").GetInt32());
                Assert.Equal(days == 1 ? 100 : 1810, report.GetProperty("you").GetProperty("spokenWords").GetInt64());
            }
            Login(admin, "admin");
            foreach (var hours in new[] { 24, 168, 720 })
                Assert.Equal(hours / 24, (await Get($"/api/admin/speaking-leaderboard?hours={hours}&until={cutoff}")).GetProperty("periodDays").GetInt32());
        } finally {
            using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.AppSettings.Where(s => s.Key == SpeakingLeaderboardSettings.Key).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == admin || u.Id == learner || u.Id == other).ExecuteDeleteAsync();
            await app.StopAsync();
        }
    }

    public sealed class UserAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers["X-Test-User"], out var user)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", user.ToString()), new Claim(ClaimTypes.Role, Request.Headers["X-Test-Role"].ToString())
            ], "test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "test")));
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.WebsiteAnalytics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public sealed class WebsiteAnalyticsIntegrationTests
{
    [LocalAnalyticsFact]
    public async Task PostgresIngestionAndAdminReport()
    {
        // Run explicitly against a disposable local database after applying migrations.
        var connection = Environment.GetEnvironmentVariable("BANTERA_ANALYTICS_TEST_DB");
        Assert.False(string.IsNullOrEmpty(connection));
        Assert.Contains("Host=localhost", connection);
        Assert.Contains("analytics_verify", connection);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["WebsiteAnalytics:IngestKey"] = new string('x', 40) });
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("website-analytics", p => { p.PermitLimit = 100; p.Window = TimeSpan.FromMinutes(1); }));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("admin")));
        await using var app = builder.Build();
        app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
        WebsiteAnalyticsEndpoints.Map(app);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/website-analytics")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Role", "user");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/website-analytics")).StatusCode);
        var session = Guid.NewGuid();
        var e = new WebsiteEventInput(Guid.NewGuid(), session, "page_view", "/learn/spanish?search=secret", "/learn/spanish", "", "chatgpt.com", "referral", "integration-test", "es-ES", "mobile");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/website-analytics/events", new[] { e })).StatusCode);
        client.DefaultRequestHeaders.Add("X-Website-Analytics-Key", new string('x', 40));
        foreach (var _ in Enumerable.Range(0, 2)) Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/website-analytics/events", new[] { e })).StatusCode);
        var play = e with { Id = Guid.NewGuid(), Name = "lesson_play" };
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/website-analytics/events", new[] { play })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/website-analytics/events", new[] { e with { Id = Guid.NewGuid(), Path = "/dashboard/users" } })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Role"); client.DefaultRequestHeaders.Add("X-Test-Role", "admin");
        using var response = await client.GetAsync("/api/admin/website-analytics?days=7&source=chatgpt.com&language=es-es");
        response.EnsureSuccessStatusCode();
        using var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(report.RootElement.GetProperty("sessions").GetInt32() >= 1);
        Assert.Contains(report.RootElement.GetProperty("sources").EnumerateArray(), s => s.GetProperty("evidence").GetString() == "campaign tag" && s.GetProperty("plays").GetInt32() >= 1);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Set<WebsiteEvent>().Where(x => x.SessionId == session).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, x => Assert.Equal("/learn/spanish", x.Path));
        await db.Set<WebsiteEvent>().Where(x => x.SessionId == session).ExecuteDeleteAsync();
        await app.StopAsync();
    }
    public sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (role.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "test")));
        }
    }
}

public sealed class LocalAnalyticsFactAttribute : FactAttribute
{
    public LocalAnalyticsFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BANTERA_ANALYTICS_TEST_DB")))
            Skip = "Set BANTERA_ANALYTICS_TEST_DB to a disposable local analytics_verify database with migrations applied.";
    }
}

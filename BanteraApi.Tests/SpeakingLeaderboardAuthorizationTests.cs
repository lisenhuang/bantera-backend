using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BanteraApi.Database;
using BanteraApi.Leaderboards;
using BanteraApi.Practice;
using BanteraApi.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class SpeakingLeaderboardAuthorizationTests
{
    [Fact]
    public async Task AdminPeriodsRequireAdminWhileLegacyValidationIsPreserved()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Database=unused"));
        builder.Services.AddScoped<SpeakingLeaderboardService>();
        builder.Services.AddSingleton<IOptions<R2Settings>>(Options.Create(new R2Settings { AccountId="test", AccessKeyId="test", SecretAccessKey="test", BucketName="test" }));
        builder.Services.AddSingleton<R2StorageService>();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, RoleAuth>("test", _ => {});
        builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("admin")));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); SpeakingLeaderboardEndpoints.Map(app); PracticeEndpoints.MapAdmin(app);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/speaking-leaderboard?days=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/api/admin/practice/exposures/00000000-0000-0000-0000-000000000001")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Role", "user");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/admin/practice/exposures/00000000-0000-0000-0000-000000000001")).StatusCode);
        foreach (var days in new[] {1, 7, 30})
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/admin/speaking-leaderboard?days={days}")).StatusCode);
        foreach (var hours in new[] {24, 168, 720})
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/admin/speaking-leaderboard?hours={hours}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/speaking-leaderboard?hours=24")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/speaking-leaderboard?hours=720&days=30")).StatusCode);
        // The released public route still rejects today and keeps its old contract.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/speaking-leaderboard?days=1")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Role");
        client.DefaultRequestHeaders.Add("X-Test-Role", "admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/speaking-leaderboard?days=2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/speaking-leaderboard?days=1&offset=-1")).StatusCode);
        await app.StopAsync();
    }

    public sealed class RoleAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (role.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var user = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", "00000000-0000-0000-0000-000000000001"),
                new Claim(ClaimTypes.Role, role)
            ], "test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, "test")));
        }
    }
}

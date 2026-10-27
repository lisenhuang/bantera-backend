using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Practice;
using BanteraApi.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
namespace BanteraApi.Tests;

public class MaoriPracticeEndpointTests
{
    [AiDatabaseFact]
    public async Task AdminListeningLevelAndEnglishAreOwnedWithoutAwardingProgress()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("Database=bantera_ai_verify_", connection);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, SpeakingLeaderboardAuthorizationTests.RoleAuth>("test", _=>{});
        builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("admin")));
        builder.Services.AddSingleton(sp => new PracticeWorker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PracticeWorker>.Instance, null!));
        builder.Services.AddSingleton(new R2StorageService(Options.Create(new R2Settings { AccountId="test", BucketName="test", AccessKeyId="test", SecretAccessKey="test" }), NullLogger<R2StorageService>.Instance));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); PracticeEndpoints.Map(app);
        var owner = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var d = new PracticeDialogue { ContentVersion=PracticeRules.ContentVersion, Ready=true, Locale="mi-NZ", Language="mi", Level=5,
            Cue="Kei hea?", Reply="Kei konei.", Distractor="Āpōpō.", CueAudio="test.wav", ReplyAudio="test.wav", DistractorAudio="test.wav", Fingerprint=Guid.NewGuid().ToString(),
            MaoriEnglishJson=JsonSerializer.Serialize(new MaoriEnglishTranslation("Where?", "Here.", "Tomorrow.")) };
        using(var scope=app.Services.CreateScope()) {
            var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Add(new User { Id=owner, Role="admin", Status="active" }); db.Add(d); await db.SaveChangesAsync();
        }
        await app.StartAsync();
        using var client=new HttpClient { BaseAddress=new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("X-Practice-Consent","1");
        client.DefaultRequestHeaders.Add("X-Test-Role","user");
        var request=new {requestId=Guid.NewGuid(),locale="mi-NZ",supportsA0=true,useSelectedLevel=true,deferLevelScoring=true,listeningLevel="C2"};
        try {
            Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/me/practice/next",request)).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Role"); client.DefaultRequestHeaders.Add("X-Test-Role","admin");
            var response=await client.PostAsJsonAsync("/api/me/practice/next",request);
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            var body=await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("C2",body.GetProperty("level").GetString()); Assert.True(body.GetProperty("reviewMode").GetBoolean());
            Assert.Equal("en-NZ",body.GetProperty("translationLocale").GetString());
            Assert.Equal("Where?",body.GetProperty("translations").GetProperty("cue").GetString());
            var id=body.GetProperty("id").GetGuid();
            var resume=await client.PostAsJsonAsync("/api/me/practice/next",new {requestId=Guid.NewGuid(),locale="mi-NZ",supportsA0=true,listeningLevel="C2"});
            Assert.Equal(id,(await resume.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
            Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/me/practice/{id}/english-translation",new {})).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,(await client.PostAsJsonAsync($"/api/me/practice/{Guid.NewGuid()}/english-translation",new {})).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Role"); client.DefaultRequestHeaders.Add("X-Test-Role","user");
            Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync($"/api/me/practice/{id}/english-translation",new {})).StatusCode);
            using var scope=app.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var progress=await db.Set<PracticeProgress>().SingleAsync(p=>p.UserId==owner);
            Assert.Equal(-1,progress.Level); Assert.Equal(-2,progress.HighestPassedLevel);
        } finally {
            using var scope=app.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u=>u.Id==owner).ExecuteDeleteAsync(); await db.Set<PracticeDialogue>().Where(x=>x.Id==d.Id).ExecuteDeleteAsync();
            await app.StopAsync();
        }
    }
}

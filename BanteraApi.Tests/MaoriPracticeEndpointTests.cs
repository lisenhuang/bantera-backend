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
            await db.Database.MigrateAsync();
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
    [AiDatabaseFact]
    public async Task AdminFlagDefaultsOffPreservesLegacyUpdatesAndGatesOwnedPractice()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("Database=bantera_ai_verify_", connection);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, SpeakingLeaderboardAuthorizationTests.RoleAuth>("test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("admin")));
        builder.Services.AddSingleton(sp => new PracticeWorker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PracticeWorker>.Instance, null!));
        builder.Services.AddSingleton(new R2StorageService(Options.Create(new R2Settings { AccountId = "test", BucketName = "test", AccessKeyId = "test", SecretAccessKey = "test" }), NullLogger<R2StorageService>.Instance));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); PracticeEndpoints.Map(app); PracticeSettings.MapPublic(app);
        var owner = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var d = new PracticeDialogue { ContentVersion = PracticeRules.ContentVersion, Ready = true, Locale = "mi-NZ", Language = "mi", Level = -1,
            Cue = "Kei hea?", Reply = "Kei konei.", Distractor = "Āpōpō.", CueAudio = "test.wav", ReplyAudio = "test.wav", DistractorAudio = "test.wav", Fingerprint = Guid.NewGuid().ToString() };
        var english = new MaoriEnglishTranslation("Where?", "Here.", "Tomorrow.", new([new("Kei hea", "Where")], [new("Kei konei", "Here")], [new("Āpōpō", "Tomorrow")]));
        d.MaoriEnglishJson = JsonSerializer.Serialize(MaoriPracticeTranslation.Prepare(english, d.Cue, d.Reply, d.Distractor));
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            await db.AppSettings.Where(s => s.Key == PracticeSettings.MaoriPublicKey).ExecuteDeleteAsync();
            db.Add(new User { Id = owner, Name = "Learner", Role = "user", Status = "active" }); db.Add(d); await db.SaveChangesAsync();
        }
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("X-Practice-Consent", "1");
        async Task<bool> PublicFlag() => (await client.GetFromJsonAsync<JsonElement>("/api/public/language-availability")).GetProperty("maoriPublicEnabled").GetBoolean();
        async Task<bool> Listed() => (await client.GetFromJsonAsync<JsonElement>("/api/public/learning-languages")).EnumerateArray().Any(x => x.GetProperty("identifier").GetString() == "mi-NZ");
        void Role(string role) { client.DefaultRequestHeaders.Remove("X-Test-Role"); client.DefaultRequestHeaders.Add("X-Test-Role", role); }
        var request = new { requestId = Guid.NewGuid(), locale = "mi-NZ", supportsA0 = true, deferLevelScoring = true, listeningLevel = "A0" };
        try {
            Assert.False(await PublicFlag()); Assert.False(await Listed());
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/practice-settings")).StatusCode);
            Role("user");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/admin/practice-settings", new { repeatWindow = 30, maoriPublicEnabled = true })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/me/practice/next", request)).StatusCode);
            using (var scope = app.Services.CreateScope()) {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var profile = new BanteraApi.Profile.ProfileService(db, null!, null!, null!, NullLogger<BanteraApi.Profile.ProfileService>.Instance, null!, null!);
                var denied = await profile.UpdateProfileAsync(owner, null, null, null, "mi-NZ", new Microsoft.AspNetCore.Http.DefaultHttpContext());
                Assert.Null(denied.Response); Assert.NotNull(denied.ErrorCode);
            }
            Role("admin");
            var enabled = await client.PutAsJsonAsync("/api/admin/practice-settings", new { repeatWindow = 30, maoriPublicEnabled = true });
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            Assert.True((await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("maoriPublicEnabled").GetBoolean());
            Assert.True(await PublicFlag()); Assert.True(await Listed());
            // An older dashboard saves its existing setting without resetting visibility.
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/admin/practice-settings", new { repeatWindow = 50 })).StatusCode);
            Assert.True(await PublicFlag());
            using (var scope = app.Services.CreateScope()) {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var profile = new BanteraApi.Profile.ProfileService(db, null!, null!, null!, NullLogger<BanteraApi.Profile.ProfileService>.Instance, null!, null!);
                var allowed = await profile.UpdateProfileAsync(owner, null, null, null, "MI_nz", new Microsoft.AspNetCore.Http.DefaultHttpContext());
                Assert.Null(allowed.ErrorCode); Assert.Equal("mi-NZ", allowed.Response!.LearningLanguage);
                var native = await profile.UpdateProfileAsync(owner, null, null, "mi-NZ", null, new Microsoft.AspNetCore.Http.DefaultHttpContext());
                Assert.NotNull(native.ErrorCode);
            }
            Role("user");
            var response = await client.PostAsJsonAsync("/api/me/practice/next", request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var id = body.GetProperty("id").GetGuid();
            Assert.Equal("ready", body.GetProperty("translationAlignmentStatus").GetString());
            var range = body.GetProperty("translationAlignments").GetProperty("cue")[0];
            Assert.Equal("Kei hea", range.GetProperty("sourceText").GetString());
            Assert.Equal(0, range.GetProperty("sourceStart").GetInt32());
            Assert.Equal("Where", range.GetProperty("targetText").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/me/practice/{id}/english-translation", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/me/practice/{Guid.NewGuid()}/english-translation", new { })).StatusCode);
            // Existing translation-only rows return immediately while a bounded repair is queued.
            using (var scope = app.Services.CreateScope()) {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var json = JsonSerializer.Serialize(english with { Alignments = null });
                await db.Set<PracticeDialogue>().Where(x => x.Id == d.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MaoriEnglishJson, json));
            }
            var preparing = await (await client.PostAsJsonAsync($"/api/me/practice/{id}/english-translation", new { })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ready", preparing.GetProperty("status").GetString());
            Assert.Equal("preparing", preparing.GetProperty("translationAlignmentStatus").GetString());
            Assert.Equal("Where?", preparing.GetProperty("translations").GetProperty("cue").GetString());
            using (var scope = app.Services.CreateScope()) {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var json = JsonSerializer.Serialize(english with { Alignments = null, AlignmentAttempts = MaoriPracticeTranslation.MaximumAlignmentAttempts });
                await db.Set<PracticeDialogue>().Where(x => x.Id == d.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MaoriEnglishJson, json));
            }
            var stopped = await (await client.PostAsJsonAsync($"/api/me/practice/{id}/english-translation", new { })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("unavailable", stopped.GetProperty("translationAlignmentStatus").GetString());
            Assert.Equal("Where?", stopped.GetProperty("translations").GetProperty("cue").GetString());
            Role("admin");
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/admin/practice-settings", new { repeatWindow = 30, maoriPublicEnabled = false })).StatusCode);
            Assert.False(await PublicFlag()); Assert.False(await Listed());
            Role("user");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/me/practice/next", request)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/me/practice/{id}/english-translation", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/me/practice/{id}/review", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/me/practice/next", new { requestId = request.requestId, locale = "en-NZ", supportsA0 = true })).StatusCode);
            Role("admin");
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/me/practice/{id}/english-translation", new { })).StatusCode);
        } finally {
            await app.StopAsync();
            using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.AppSettings.Where(s => s.Key == PracticeSettings.MaoriPublicKey || s.Key == PracticeSettings.Key).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner).ExecuteDeleteAsync(); await db.Set<PracticeDialogue>().Where(x => x.Id == d.Id).ExecuteDeleteAsync();
        }
    }

}

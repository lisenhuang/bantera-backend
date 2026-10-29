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

public class PracticeRotationTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(100)]
    public void WindowIsScopedToUserLanguageAndLevel(int window)
    {
        var owner = Guid.NewGuid();
        var time = DateTime.UtcNow;
        PracticeExposure Entry(int i, Guid user, string language, int level) => new() {
            UserId = user, SeenAt = time.AddMinutes(i), DialogueId = Guid.NewGuid(),
            Dialogue = new() { Language = language, Level = level }
        };
        var matching = Enumerable.Range(0, window + 1).Select(i => Entry(i, owner, "en", -1)).ToArray();
        var other = new[] { Entry(100, Guid.NewGuid(), "en", -1), Entry(101, owner, "ja", -1), Entry(102, owner, "en", 0) };
        var recent = PracticeRotation.RecentIds(matching.Concat(other).AsQueryable(), owner, "en", -1, window).ToArray();
        Assert.Equal(window, recent.Length);
        Assert.DoesNotContain(matching[0].DialogueId, recent);
        Assert.All(matching.Skip(1), e => Assert.Contains(e.DialogueId, recent));
        Assert.All(other, e => Assert.DoesNotContain(e.DialogueId, recent));
        // Count presentations, not distinct IDs; old versions can have histories
        // that include multiple presentations of the same exercise.
        matching[window].DialogueId = matching[window - 1].DialogueId;
        Assert.Equal(window, PracticeRotation.RecentIds(matching.AsQueryable(), owner, "en", -1, window).Count());
    }

    [Fact]
    public void GenerationContextUsesBoundedSituationLabelsNotFullExercises()
    {
        var rows = Enumerable.Range(0, 40).Select(i => new PracticeDialogue {
            Locale = "en-NZ", Level = -1, Topic = "food", MeaningKey = $"situation {i} " + new string('x', 200),
            Cue = "This full question should not enter the prompt.", Reply = "Neither should this full reply.",
            CreatedAt = DateTime.UtcNow.AddMinutes(i)
        }).ToList();
        rows.Add(new() { Locale = "ja-JP", Level = -1, MeaningKey = "wrong language", CreatedAt = DateTime.UtcNow.AddDays(1) });
        rows.Add(new() { Locale = "en-NZ", Level = 0, MeaningKey = "wrong level", CreatedAt = DateTime.UtcNow.AddDays(1) });
        var descriptions = PracticeRotation.Descriptions(rows, "en-NZ", -1, 30);
        Assert.Equal(30, descriptions.Length);
        Assert.All(descriptions, d => Assert.True(d.Situation.Length <= 96));
        var json = JsonSerializer.Serialize(descriptions);
        Assert.DoesNotContain("full question", json);
        Assert.DoesNotContain("full reply", json);
        Assert.DoesNotContain("wrong", json);
        Assert.DoesNotContain("situation 9 ", json);
        Assert.Contains("situation 39 ", json);
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData("broken", 50)]
    [InlineData("501", 50)]
    [InlineData("0", 50)]
    [InlineData("30", 30)]
    [InlineData("100", 100)]
    public void SettingHasSafeDefaultAndPreservesValidValues(string? value, int expected) =>
        Assert.Equal(expected, PracticeSettings.Resolve(value));

    [AiDatabaseFact]
    public async Task NextReusesOlderAudioWithFreshAttemptsAndPreservesIdempotencyAndSavedHistory()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("bantera_ai_verify", connection);
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, WordActivityTests.TestAuth>("test", _ => {});
        builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireClaim("sub", owner.ToString())));
        builder.Services.AddSingleton(sp => new PracticeWorker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PracticeWorker>.Instance, null!));
        builder.Services.AddSingleton(new R2StorageService(Options.Create(new R2Settings {
            AccountId = "test", BucketName = "test", AccessKeyId = "test", SecretAccessKey = "test"
        }), NullLogger<R2StorageService>.Instance));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); PracticeEndpoints.Map(app);
        var now = DateTime.UtcNow.AddDays(-2);
        var rows = Enumerable.Range(0, 31).Select(i => new PracticeDialogue {
            Locale = "en-NZ", Language = "en", Level = -1, Ready = true, ContentVersion = PracticeRules.ContentVersion,
            Cue = $"Question {i}?", Reply = $"Answer {i}", Distractor = "Wrong", Topic = "food",
            Fingerprint = Guid.NewGuid().ToString(), CueAudio = $"original-{i}.wav", ReplyAudio = "reply.wav", DistractorAudio = "wrong.wav"
        }).ToArray();
        var old = rows.Select((d, i) => new PracticeExposure {
            UserId = owner, Dialogue = d, DialogueId = d.Id, RequestId = Guid.NewGuid(), SeenAt = now.AddMinutes(i),
            FinishedAt = now.AddMinutes(i + 1), Attempts = 2, FirstCorrect = true, DeferredScoring = true
        }).ToArray();
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            await PracticeSettings.SaveAsync(db, 30, owner, default);
            db.Users.AddRange(new User { Id = owner, Status = "active" }, new User { Id = other, Status = "active" });
            db.AddRange(old);
            db.Add(new PracticeProgress { UserId = owner, Language = "en", Level = -1 });
            await db.SaveChangesAsync();
            Assert.Equal(rows[0].Id, (await PracticeRotation.Available(db, owner, "en-NZ", "en", -1, 30).SingleAsync()).Id);
            Assert.Equal(31, await PracticeRotation.Available(db, other, "en-NZ", "en", -1, 30).CountAsync());
        }
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("X-Test-User", owner.ToString());
        client.DefaultRequestHeaders.Add("X-Practice-Consent", "1");
        try {
            using var anonymous = new HttpClient { BaseAddress = client.BaseAddress };
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/practice-settings")).StatusCode);
            anonymous.DefaultRequestHeaders.Add("X-Test-User", other.ToString());
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync("/api/admin/practice-settings")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PutAsJsonAsync("/api/admin/practice-settings", new { repeatWindow = 100 })).StatusCode);
            foreach (var invalid in new[] { 0, 9, 501 })
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/admin/practice-settings", new { repeatWindow = invalid })).StatusCode);
            foreach (var value in new[] { 50, 100, 30 }) {
                Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/admin/practice-settings", new { repeatWindow = value })).StatusCode);
                var settings = await client.GetFromJsonAsync<JsonElement>("/api/admin/practice-settings");
                Assert.Equal(value, settings.GetProperty("repeatWindow").GetInt32());
                using var verification = app.Services.CreateScope();
                var checkDb = verification.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(value, await PracticeSettings.GetAsync(checkDb, default));
                Assert.Equal(value == 30 ? 1 : 0, await PracticeRotation.Available(checkDb, owner, "en-NZ", "en", -1, value).CountAsync());
            }
            var requestId = Guid.NewGuid();
            async Task<JsonElement> Next(Guid request, Guid? previous) {
                var response = await client.PostAsJsonAsync("/api/me/practice/next", new {
                    requestId = request, locale = "en-NZ", previousId = previous, deferLevelScoring = true, supportsA0 = true
                });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return await response.Content.ReadFromJsonAsync<JsonElement>();
            }
            var first = await Next(requestId, old[30].Id);
            var id = first.GetProperty("id").GetGuid();
            Assert.NotEqual(old[0].Id, id);
            Assert.Equal(rows[0].Cue, first.GetProperty("cue").GetString());
            Assert.Contains("original-0.wav", first.GetProperty("cueAudioUrl").GetString());
            Assert.Equal(id, (await Next(requestId, old[30].Id)).GetProperty("id").GetGuid());
            Assert.Equal(id, (await Next(Guid.NewGuid(), null)).GetProperty("id").GetGuid());
            var saved = await client.PostAsJsonAsync($"/api/me/practice/{old[0].Id}/review", new {});
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var second = await Next(Guid.NewGuid(), id);
            Assert.Equal(rows[1].Cue, second.GetProperty("cue").GetString());
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(33, await db.Set<PracticeExposure>().CountAsync(e => e.UserId == owner));
            var original = await db.Set<PracticeExposure>().SingleAsync(e => e.Id == old[0].Id);
            Assert.Equal(2, original.Attempts); Assert.True(original.FirstCorrect);
            var replay = await db.Set<PracticeExposure>().SingleAsync(e => e.Id == id);
            Assert.Equal(0, replay.Attempts); Assert.Null(replay.FirstCorrect);
            Assert.Equal(-1, (await db.Set<PracticeProgress>().SingleAsync(p => p.UserId == owner)).Level);
            Assert.DoesNotContain(rows[0].Id, await PracticeRotation.Available(db, owner, "en-NZ", "en", -1, 30).Select(d => d.Id).ToArrayAsync());
            // A small pool must wait for preparation instead of violating the window.
            await db.Set<PracticeDialogue>().Where(d => d.Id == rows[2].Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Ready, false));
            var empty = await client.PostAsJsonAsync("/api/me/practice/next", new {
                requestId = Guid.NewGuid(), locale = "en-NZ", previousId = second.GetProperty("id").GetGuid(), supportsA0 = true
            });
            Assert.Equal(HttpStatusCode.Accepted, empty.StatusCode);
        } finally {
            await app.StopAsync();
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.AppSettings.Where(s => s.Key == PracticeSettings.Key).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner || u.Id == other).ExecuteDeleteAsync();
            var ids = rows.Select(d => d.Id).ToArray();
            await db.Set<PracticeDialogue>().Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync();
        }
    }
}

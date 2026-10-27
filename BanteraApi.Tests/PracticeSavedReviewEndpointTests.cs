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

public class PracticeSavedReviewEndpointTests
{
    [AiDatabaseFact]
    public async Task SavedReviewRefreshesAudioJudgesAnswersAndPreservesActivePractice()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("bantera_ai_verify", connection);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, WordActivityTests.TestAuth>("test", _ => {});
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(sp => new PracticeWorker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PracticeWorker>.Instance, null!));
        builder.Services.AddSingleton(new R2StorageService(Options.Create(new R2Settings {
            AccountId = "test", BucketName = "test", AccessKeyId = "test", SecretAccessKey = "test"
        }), NullLogger<R2StorageService>.Instance));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); PracticeEndpoints.Map(app);
        var userId = Guid.NewGuid(); var outsider = Guid.NewGuid();
        var dialogue = new PracticeDialogue { ContentVersion = PracticeRules.ContentVersion, Ready = true,
            Language = "en", Locale = "en-US", Level = 0, Cue = "Hello?", Reply = "Hello", Distractor = "Tomorrow",
            CueAudio = "cue.wav", ReplyAudio = "reply.wav", DistractorAudio = "wrong.wav", Fingerprint = Guid.NewGuid().ToString() };
        var original = new PracticeExposure { UserId = userId, Dialogue = dialogue, DialogueId = dialogue.Id,
            RequestId = Guid.NewGuid(), Attempts = 10, DeferredScoring = true, Swap = true };
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(new User { Id = userId, Status = "active" }, new User { Id = outsider, Status = "active" });
            db.Add(original);
            db.Add(new PracticeProgress { UserId = userId, Language = "en", Level = 0, Window = "11111111111" });
            await db.SaveChangesAsync();
        }
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        try {
            var route = $"/api/me/practice/{original.Id}/review";
            var request = new { requestId = Guid.NewGuid() };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(route, request)).StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-User", outsider.ToString());
            client.DefaultRequestHeaders.Add("X-Practice-Consent", "1");
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(route, request)).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-User");
            client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
            client.DefaultRequestHeaders.Remove("X-Practice-Consent");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(route, request)).StatusCode);
            client.DefaultRequestHeaders.Add("X-Practice-Consent", "1");
            var response = await client.PostAsJsonAsync(route, request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var review = await response.Content.ReadFromJsonAsync<JsonElement>();
            var id = review.GetProperty("id").GetGuid();
            Assert.Equal(original.Id, id);
            Assert.True(review.GetProperty("reviewMode").GetBoolean());
            Assert.Contains("X-Amz-Signature", review.GetProperty("cueAudioUrl").GetString());
            Assert.Equal("Tomorrow", review.GetProperty("choices")[0].GetProperty("text").GetString());
            var retry = await (await client.PostAsJsonAsync(route, request)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(id, retry.GetProperty("id").GetGuid());
            var answer = await client.PostAsJsonAsync($"/api/me/practice/{id}/review/answer", new { attemptId = Guid.NewGuid(), transcript = "Hello" });
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            var feedback = await answer.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("correct", feedback.GetProperty("outcome").GetString());
            Assert.Equal(1, feedback.GetProperty("choice").GetInt32());
            var next = await client.PostAsJsonAsync("/api/me/practice/next", new {
                requestId = Guid.NewGuid(), locale = "en-US", deferLevelScoring = true, useSelectedLevel = true, supportsA0 = true
            });
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            Assert.Equal(original.Id, (await next.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var progress = await db.Set<PracticeProgress>().SingleAsync(x => x.UserId == userId);
            Assert.Equal(0, progress.Level); Assert.Equal("11111111111", progress.Window);
            var unchanged = (await db.Set<PracticeExposure>().FindAsync(original.Id))!;
            Assert.Equal(10, unchanged.Attempts);
            Assert.False(unchanged.TranslationUsed);
            Assert.Null(unchanged.FirstCorrect);
            Assert.Null(unchanged.FinishedAt);
            Assert.Equal(1, await db.Set<PracticeExposure>().CountAsync(x => x.UserId == userId));
            Assert.False(await db.Set<PracticeAttempt>().AnyAsync(x => x.ExposureId == original.Id));
            var pronunciationRoute = $"/api/me/practice/{id}/pronunciation";
            var unsupported = await (await client.PostAsJsonAsync(pronunciationRoute, new {})).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("unsupported", unsupported.GetProperty("status").GetString());
            var stored = await db.Set<PracticeDialogue>().SingleAsync(x => x.Id == dialogue.Id);
            stored.Locale = "zh-CN"; stored.Cue = "你"; stored.Reply = "好"; stored.Distractor = "不";
            await db.SaveChangesAsync();
            Assert.False(await PracticePronunciationWorker.Pending(db, DateTime.UtcNow).AnyAsync(x => x.Id == dialogue.Id));
            var pending = await (await client.PostAsJsonAsync(pronunciationRoute, new {})).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("preparing", pending.GetProperty("status").GetString());
            Assert.True(await PracticePronunciationWorker.Pending(db, DateTime.UtcNow).AnyAsync(x => x.Id == dialogue.Id));
            Assert.NotNull(await db.Set<PracticeDialogue>().AsNoTracking().Where(x => x.Id == dialogue.Id).Select(x => x.PronunciationRequestedAt).SingleAsync());
            stored.PronunciationJson = JsonSerializer.Serialize(new PracticePronunciationData([new("你","nǐ")], [new("好","hǎo")], [new("不","bù")]), PracticePronunciation.Json);
            await db.SaveChangesAsync();
            var ready = await (await client.PostAsJsonAsync(pronunciationRoute, new {})).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ready", ready.GetProperty("status").GetString());
            Assert.Equal("bù", ready.GetProperty("choices")[0][0].GetProperty("reading").GetString());
            // A saved broad mapping is hidden immediately, queued without
            // touching progress, then repaired once with a persisted marker.
            stored.Cue = "朋友";
            stored.PronunciationJson = JsonSerializer.Serialize(new PracticePronunciationData([new("朋友", "péng you")], [new("好", "hǎo")], [new("不", "bù")]), PracticePronunciation.Json);
            stored.PronunciationAttempts = 5;
            await db.SaveChangesAsync();
            var invalid = await (await client.PostAsJsonAsync(pronunciationRoute, new {})).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("preparing", invalid.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, invalid.GetProperty("cue").ValueKind);
            Assert.True(await PracticePronunciationWorker.Pending(db, DateTime.UtcNow).AnyAsync(x => x.Id == dialogue.Id));
            Assert.True(PracticePronunciationRepair.Inspect(stored, DateTime.UtcNow));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            stored = await db.Set<PracticeDialogue>().SingleAsync(x => x.Id == dialogue.Id);
            Assert.False(PracticePronunciationRepair.Inspect(stored, DateTime.UtcNow));
            Assert.Equal(0, stored.PronunciationAttempts);
            stored.PronunciationAttempts = 5;
            stored.PronunciationRetryAt = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
            var exhausted = await (await client.PostAsJsonAsync(pronunciationRoute, new {})).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("unavailable", exhausted.GetProperty("status").GetString());
            Assert.False(await PracticePronunciationWorker.Pending(db, DateTime.UtcNow).AnyAsync(x => x.Id == dialogue.Id));
            Assert.Equal(5, await db.Set<PracticeDialogue>().AsNoTracking().Where(x => x.Id == dialogue.Id).Select(x => x.PronunciationAttempts).SingleAsync());
            client.DefaultRequestHeaders.Remove("X-Practice-Consent");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(pronunciationRoute, new {})).StatusCode);
            client.DefaultRequestHeaders.Add("X-Practice-Consent", "1");
            client.DefaultRequestHeaders.Remove("X-Test-User"); client.DefaultRequestHeaders.Add("X-Test-User", outsider.ToString());
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(pronunciationRoute, new {})).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-User"); client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
            // Retired content cannot silently open different material.
            await db.Set<PracticeDialogue>().Where(x => x.Id == dialogue.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.ContentVersion, 0));
            Assert.Equal(HttpStatusCode.Gone, (await client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() })).StatusCode);
        } finally {
            await app.StopAsync();
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(x => x.Id == userId || x.Id == outsider).ExecuteDeleteAsync();
            await db.Set<PracticeDialogue>().Where(x => x.Id == dialogue.Id).ExecuteDeleteAsync();
        }
    }
}

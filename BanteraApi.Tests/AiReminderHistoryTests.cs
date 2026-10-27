using BanteraApi.Chat.Ai;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BanteraApi.Tests;
public class AiReminderHistoryTests
{
    [Fact]
    public void VocabularyRequestsSimpleMeaningsAndExamplesInEveryToolVersion()
    {
        Assert.Contains("very basic everyday words", AiVocabularyTool.Prompt);
        Assert.DoesNotContain("optionally a short example", AiVocabularyTool.Prompt);
        foreach (var version in new[] { 1, 2 }) {
            var json = System.Text.Json.JsonSerializer.Serialize(AiVocabularyTool.DeclarationFor(version));
            Assert.Contains("Required for add, optional for update", json);
            Assert.Contains("very basic everyday words", json);
        }
    }

    [RankingDatabaseFact]
    public async Task ClearCancelsSchedulesAndGenerationPreservesOtherUsersAndRingingAndIsIdempotent()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_RANKING_TEST_DB")!;
        Assert.Contains("bantera_word_rankings_verify", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE ai_callbacks (\"Id\" uuid PRIMARY KEY, \"UserId\" uuid NOT NULL, \"Status\" text NOT NULL);");
        var user = Guid.NewGuid(); var other = Guid.NewGuid(); var generating = Guid.NewGuid();
        foreach (var status in new[] { "scheduled", "queued", "ready", "delivered", "answered", "cancelled", "failed", "missed", "ringing" }) {
            var id = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ai_callbacks VALUES ({id}, {user}, {status})");
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ai_callbacks VALUES ({generating}, {user}, 'generating'), ({other}, {other}, 'scheduled')");
        Assert.Equal(0, await AiReminderDelivery.RemoveHistoryAsync(db, user, other, default));
        Assert.Equal(1, await AiReminderDelivery.RemoveHistoryAsync(db, user, generating, default));
        // The worker's conditional completion can no longer recreate deleted audio.
        Assert.Equal(0, await db.AiCallbacks.Where(c => c.Id == generating && c.Status == "generating")
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, "ready")));
        Assert.Equal(8, await AiReminderDelivery.RemoveHistoryAsync(db, user, null, default));
        Assert.Equal(new[] { "ringing" }, await db.AiCallbacks.Where(c => c.UserId == user).Select(c => c.Status).ToArrayAsync());
        Assert.Equal(1, await db.AiCallbacks.CountAsync(c => c.UserId == other));
        Assert.Equal(0, await AiReminderDelivery.RemoveHistoryAsync(db, user, null, default));
    }
}

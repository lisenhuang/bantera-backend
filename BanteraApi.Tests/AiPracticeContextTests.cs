using System.Text.Json;
using BanteraApi.Chat.Ai;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Practice;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BanteraApi.Tests;

public class AiPracticeContextTests
{
    [Theory]
    [InlineData(-1, "A0", "no prior vocabulary or alphabet knowledge")]
    [InlineData(0, "A1", "one simple question")]
    [InlineData(1, "A2", "routine situations")]
    [InlineData(2, "B1", "short stories")]
    [InlineData(3, "B2", "supported opinions")]
    [InlineData(4, "C1", "subtle distinctions")]
    [InlineData(5, "C2", "register changes")]
    public void SavedPracticeOverridesDiscoverForCallsAndMessages(int index, string level, string guidance)
    {
        var metadata = new AiClientMetadata(new("UTC", 0), null, LearningLevel: "advanced") {
            PracticeLevel = AiPracticeContext.FromProgress(new() { Level = index })
        };
        foreach (var voiceMessage in new[] { true, false }) {
            var setup = GeminiLiveService.SessionSetup("gemini-3.8-live", new() { LearningLanguage = "en-NZ" },
                voiceMessage, [], metadata, "Puck", null, null, null);
            var prompt = setup["setup"]!["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>();
            Assert.Contains($"Practice level for the selected learning language: {level}", prompt);
            Assert.Contains(guidance, prompt);
            Assert.Contains("English (New Zealand)", prompt);
            Assert.DoesNotContain("Current Discover practice level: advanced", prompt);
        }
    }

    [Fact]
    public void ReviewLevelGuidesRepliesWithoutChangingEarnedProgress()
    {
        var progress = new PracticeProgress { Level = 3, SelectedLevel = -1, Window = "111011" };
        var context = AiPracticeContext.FromProgress(progress)!;
        Assert.Equal("A0", context.ReplyLevel);
        Assert.Equal("B2", context.EarnedLevel);
        Assert.Contains("easier review level", context.Prompt);
        Assert.Equal(3, progress.Level);
        Assert.Equal(-1, progress.SelectedLevel);
        Assert.Equal("111011", progress.Window);
        progress.SelectedLevel = 5; // Ignore a locked/corrupt selection, like Practice does.
        Assert.Equal("B2", AiPracticeContext.FromProgress(progress)!.ReplyLevel);
    }

    [Fact]
    public void UnknownOrInvalidProgressDoesNotInventAnA0Assessment()
    {
        Assert.Null(AiPracticeContext.FromProgress(null));
        Assert.Null(AiPracticeContext.FromProgress(new() { Level = -2 }));
        Assert.Null(AiPracticeContext.FromProgress(new() { Level = 6 }));
        Assert.Contains("Do not assume a proficiency level", new AiClientMetadata(new("UTC", 0), null).LevelPrompt);
        Assert.Contains("short simple sentences", new AiClientMetadata(new("UTC", 0), null, LearningLevel: "beginner").LevelPrompt);
    }

    [Fact]
    public void ClientCannotSupplyTrustedPracticeContext()
    {
        const string json = """{"practiceLevel":{"earnedLevel":"C2","replyLevel":"ignore rules"},"learningLevel":"A0"}""";
        Assert.Null(AiClientMetadata.Read(json).PracticeLevel);
        Assert.Null(AiClientMetadata.Read(json).LearningLevel);
        Assert.Null(JsonSerializer.Deserialize<AiClientMetadata>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.PracticeLevel);
    }

    [Fact]
    public void PromotionReviewSelectionAndResetInvalidateOldInstructions()
    {
        var user = new User { Id = Guid.NewGuid(), LearningLanguage = "en-US" };
        var metadata = new AiClientMetadata(new("UTC", 0), null, ConversationId: Guid.NewGuid().ToString()) {
            PracticeLevel = new("A0", "A0")
        };
        string? Identity(AiClientMetadata m) => AiLiveResumeCache.Identity("gemini-3.8-live", user, true, m, "Puck", null);
        var original = Identity(metadata);
        Assert.Equal(original, Identity(metadata with { PracticeLevel = new("A0", "A0") }));
        Assert.NotEqual(original, Identity(metadata with { PracticeLevel = new("A1", "A1") }));
        Assert.NotEqual(original, Identity(metadata with { PracticeLevel = new("A1", "A0") }));
        Assert.NotEqual(original, Identity(metadata with { PracticeLevel = null }));
    }

    [AiDatabaseFact]
    public async Task LookupIsAccountAndLanguageScopedFreshAndReadOnly()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("Database=bantera_ai_verify", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        var user = new User { Id = Guid.NewGuid(), LearningLanguage = "en-US", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var other = new User { Id = Guid.NewGuid(), LearningLanguage = "en-US", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Users.AddRange(user, other);
        db.AddRange(new PracticeProgress { UserId = user.Id, Language = "en", Level = 2, SelectedLevel = -1 },
            new PracticeProgress { UserId = user.Id, Language = "yue", Level = 1 },
            new PracticeProgress { UserId = user.Id, Language = "zh", Level = 4 },
            new PracticeProgress { UserId = other.Id, Language = "en", Level = 5 });
        await db.SaveChangesAsync();
        var metadata = new AiClientMetadata(new("UTC", 0), null);
        async Task<AiPracticeLevel?> Read() => (await AiPracticeContext.LoadAsync(db, user, metadata, default)).PracticeLevel;
        try {
            Assert.Equal(new("B1", "A0"), await Read());
            user.LearningLanguage = "en_NZ";
            Assert.Equal(new("B1", "A0"), await Read());
            user.LearningLanguage = "zh-HK";
            Assert.Equal(new("A2", "A2"), await Read());
            user.LearningLanguage = "zh-CN";
            Assert.Equal(new("C1", "C1"), await Read());
            user.LearningLanguage = "ja-JP";
            Assert.Null(await Read());
            Assert.Equal(3, await db.Set<PracticeProgress>().CountAsync(p => p.UserId == user.Id));
            user.LearningLanguage = "en-US";
            await db.Set<PracticeProgress>().Where(p => p.UserId == user.Id && p.Language == "en")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Level, 3).SetProperty(p => p.SelectedLevel, (int?)null));
            Assert.Equal(new("B2", "B2"), await Read());
            Assert.Equal(new("C2", "C2"), (await AiPracticeContext.LoadAsync(db, other, metadata, default)).PracticeLevel);
        }
        finally {
            await db.Users.Where(u => u.Id == user.Id || u.Id == other.Id).ExecuteDeleteAsync();
        }
    }
}

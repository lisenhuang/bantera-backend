using System.Text.Json;
using BanteraApi.Chat.Ai;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class AiLiveReasoningTests
{
    [Theory]
    [InlineData("gemini-3.1-flash-live-preview", "minimal")]
    [InlineData("gemini-3.1-flash-live-preview", "high")]
    [InlineData("gemini-3.8-live-extended-thinking", "medium")]
    [InlineData("models/gemini-3.8-live-extended-thinking", "high")]
    public void LevelIsSentForBothCallsAndVoiceMessages(string model, string level)
    {
        foreach (var voiceMessage in new[] { false, true }) {
            var setup = JsonSerializer.SerializeToElement(GeminiLiveService.Setup(model, "prompt", voiceMessage, reasoning: level)).GetProperty("setup");
            var generation = setup.GetProperty("generationConfig");
            Assert.Equal(level, generation.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
            Assert.False(generation.GetProperty("thinkingConfig").TryGetProperty("thinkingBudget", out _));
            Assert.False(generation.TryGetProperty("maxOutputTokens", out _));
            Assert.Equal(new[] { "AUDIO" }, generation.GetProperty("responseModalities").EnumerateArray().Select(x => x.GetString()));
        }
    }

    [Theory]
    [InlineData("off", 0)]
    [InlineData("dynamic", -1)]
    [InlineData("1024", 1024)]
    [InlineData("24576", 24576)]
    public void LegacyNativeAudioUsesBudgetInsteadOfLevel(string value, int budget)
    {
        var config = JsonSerializer.SerializeToElement(AiLiveReasoning.Config("gemini-2.5-flash-native-audio-preview-12-2025", value));
        Assert.Equal(budget, config.GetProperty("thinkingBudget").GetInt32());
        Assert.False(config.TryGetProperty("thinkingLevel", out _));
    }

    [Theory]
    [InlineData("gemini-3.8-live", "high")]
    [InlineData("gemini-3.8-live-extended-thinking", "minimal")]
    [InlineData("gemini-3.1-flash-live-preview", "4096")]
    [InlineData("gemini-2.5-flash-native-audio-preview-09-2025", "high")]
    [InlineData("future-live-model", "low")]
    public void IncompatibleOrCorruptSavedValuesFallBackSafely(string model, string value)
    {
        Assert.False(AiLiveReasoning.IsSupported(model, value));
        Assert.Equal("default", AiLiveReasoning.EffectiveValue(model, value));
        Assert.Equal(JsonSerializer.Serialize(AiLiveReasoning.Config(model, null)), JsonSerializer.Serialize(AiLiveReasoning.Config(model, value)));
    }

    [Fact]
    public void DefaultsPreserveExistingWireContractAndKeysAreBounded()
    {
        Assert.Null(AiLiveReasoning.Config("gemini-3.8-live", null));
        Assert.Null(AiLiveReasoning.Config("gemini-3.1-flash-live-preview", null));
        Assert.Null(AiLiveReasoning.Config("future-live-model", null));
        Assert.Equal("low", JsonSerializer.SerializeToElement(AiLiveReasoning.Config("gemini-3.8-live-extended-thinking", null)).GetProperty("thinkingLevel").GetString());
        Assert.Equal(BanteraAiSettings.ReasoningKey("models/gemini-3.8-live"), BanteraAiSettings.ReasoningKey("gemini-3.8-live"));
        Assert.True(BanteraAiSettings.ReasoningKey(new string('x', 100)).Length <= 100);
        var oldRequest = JsonSerializer.Deserialize<BanteraAiEndpoints.ModelRequest>("{\"Model\":\"gemini-3.8-live\"}");
        Assert.Null(oldRequest!.Reasoning);
    }

    [AiDatabaseFact]
    public async Task SettingsSurviveReloadModelSwitchAndLegacyWrites()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("Database=bantera_ai_verify", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        const string a = "gemini-3.1-flash-live-preview", b = "gemini-3.8-live-extended-thinking";
        var keys = new[] { BanteraAiSettings.ModelKey, BanteraAiSettings.VoiceKey, BanteraAiSettings.ReasoningKey(a), BanteraAiSettings.ReasoningKey(b) };
        var original = await db.AppSettings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToListAsync();
        var settings = new BanteraAiSettings(db, Options.Create(new BanteraAiOptions()));
        var admin = Guid.NewGuid();
        try {
            await settings.SetAsync(a, "Puck", admin, default, "minimal");
            await settings.SetAsync(b, "Kore", admin, default, "high");
            await settings.SetModelAsync(a, admin, default); // Old clients do not send reasoning or voice.
            await using var reloadedDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
            var reloaded = new BanteraAiSettings(reloadedDb, Options.Create(new BanteraAiOptions()));
            Assert.Equal(a, await reloaded.GetModelAsync(default));
            Assert.Equal("Kore", await reloaded.GetVoiceAsync(default));
            var selections = await reloaded.GetReasoningSelectionsAsync([a, b, "gemini-3.8-live"], default);
            Assert.Equal("minimal", selections[a]); Assert.Equal("high", selections[b]); Assert.Equal("default", selections["gemini-3.8-live"]);
            await Assert.ThrowsAsync<ArgumentException>(() => settings.SetAsync(b, "Puck", admin, default, "minimal"));
            Assert.Equal(a, await settings.GetModelAsync(default));
            Assert.Equal("Kore", await settings.GetVoiceAsync(default));
            await settings.SetAsync(a, null, admin, default, "default");
            Assert.Equal("default", await settings.GetReasoningAsync(a, default));
        }
        finally {
            await db.AppSettings.Where(s => keys.Contains(s.Key)).ExecuteDeleteAsync();
            db.AppSettings.AddRange(original);
            await db.SaveChangesAsync();
        }
    }
}

using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Gemini;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BanteraApi.Tests;

public class HistoricalDialogueDurationPlannerTests
{
    private static DialogueRateSample Sample(int units, string locale = "en-NZ",
        string? level = "intermediate", string? model = "tts") =>
        new(locale, level, locale.StartsWith("zh") || locale.StartsWith("yue")
            ? new string('好', units) : string.Join(" ", Enumerable.Repeat("hello", units)), 60000, model);

    [Fact]
    public void PrefersExactLocaleAndModelAndUsesMedianDespiteOutlier()
    {
        var samples = new[] { Sample(150), Sample(160), Sample(170), Sample(900) }
            .Concat(Enumerable.Repeat(Sample(250, model: "old-tts"), 5))
            .Concat(Enumerable.Repeat(Sample(300, locale: "en-US"), 5));
        var plan = HistoricalDialogueDurationPlanner.SelectPlan("EN_nz", 180, "intermediate", "tts", samples);
        Assert.Equal(165, plan.UnitsPerMinute);
        Assert.Equal(495, plan.TargetUnits);
        Assert.Equal("locale_model_history", plan.RateSource);
        Assert.Equal(4, plan.HistorySampleCount);
    }

    [Fact]
    public void ExactLocaleWithoutModelAttributionBeatsOtherAccents()
    {
        var samples = Enumerable.Repeat(Sample(160, model: null), 3)
            .Concat(Enumerable.Repeat(Sample(300, locale: "en-US"), 4));
        var plan = HistoricalDialogueDurationPlanner.SelectPlan("en-NZ", 180, "intermediate", "tts", samples);
        Assert.Equal(480, plan.TargetUnits);
        Assert.Equal("locale_history", plan.RateSource);
    }

    [Theory]
    [InlineData("tts", "language_model_history")]
    [InlineData(null, "language_history")]
    public void SparseAccentUsesSameSpokenLanguage(string? model, string source)
    {
        var samples = new[] { Sample(100) }
            .Concat(Enumerable.Repeat(Sample(160, locale: "en-AU", model: model), 3))
            .Concat(Enumerable.Repeat(Sample(280, locale: "fr-FR"), 5));
        var plan = HistoricalDialogueDurationPlanner.SelectPlan("en-NZ", 120, "intermediate", "tts", samples);
        Assert.Equal(320, plan.TargetUnits);
        Assert.Equal(source, plan.RateSource);
    }

    [Fact]
    public void BeginnerHistoryIsSeparateAndIsNotSlowedTwice()
    {
        var samples = Enumerable.Repeat(Sample(100, level: "beginner"), 3)
            .Concat(Enumerable.Repeat(Sample(180), 5));
        var plan = HistoricalDialogueDurationPlanner.SelectPlan("en-NZ", 120, "beginner", "tts", samples);
        Assert.Equal(200, plan.TargetUnits);
        Assert.Equal(3, plan.HistorySampleCount);
    }

    [Theory]
    [InlineData("zh-HK", "yue-CN", "zh-CN")]
    [InlineData("yue-CN", "zh-HK", "zh-TW")]
    [InlineData("zh-TW", "zh-CN", "zh-HK")]
    public void CantoneseAndMandarinDoNotShareHistory(string locale, string related, string unrelated)
    {
        var samples = Enumerable.Repeat(Sample(200, related), 3)
            .Concat(Enumerable.Repeat(Sample(450, unrelated), 7));
        var plan = HistoricalDialogueDurationPlanner.SelectPlan(locale, 120, "intermediate", "tts", samples);
        Assert.Equal(400, plan.TargetUnits);
        Assert.True(plan.CountCharacters);
        Assert.Equal(3, plan.HistorySampleCount);
    }

    [Fact]
    public void InsufficientOrInvalidHistoryKeepsInitialEstimate()
    {
        var samples = new[]
        {
            Sample(160), Sample(160), Sample(0), Sample(2000),
            Sample(160) with { DurationMs = 0 }, Sample(160) with { DurationMs = 1800001 },
            Sample(160, level: "beginner"), Sample(160, locale: "fr-FR"),
        };
        var plan = HistoricalDialogueDurationPlanner.SelectPlan("en-NZ", 120, "intermediate", "tts", samples);
        Assert.Equal(350, plan.TargetUnits);
        Assert.Equal("language_baseline", plan.RateSource);
        Assert.Equal(0, plan.HistorySampleCount);
    }

    [Fact]
    public void MeasuresActualMinutesAndKeepsLegacyLevelAsIntermediate()
    {
        var samples = Enumerable.Repeat(Sample(320, level: null) with { DurationMs = 120000 }, 3);
        var plan = HistoricalDialogueDurationPlanner.SelectPlan("en-NZ", 180, "intermediate", "tts", samples);
        Assert.Equal(480, plan.TargetUnits);
        Assert.Contains("160 words per minute", plan.PromptInstruction);
        Assert.Contains("Script length target: 480 words", plan.PromptInstruction);
    }

    [Fact]
    public void OnlyEligibleSavedAudioIsUsed()
    {
        UserVideo Valid() => new()
        {
            IsAiGenerated = true, MediaContentType = "audio/mpeg", FileSizeBytes = 10000,
            DurationMs = 60000, TranscriptText = "hello", Level = null,
        };
        var valid = Valid();
        var estimated = Valid(); estimated.IsTranscriptionEstimated = true;
        var human = Valid(); human.IsAiGenerated = false;
        var video = Valid(); video.MediaContentType = "video/mp4";
        var empty = Valid(); empty.FileSizeBytes = 0;
        var noTranscript = Valid(); noTranscript.TranscriptText = "";
        var tiny = Valid(); tiny.DurationMs = 100;
        var beginner = Valid(); beginner.Level = "beginner";
        var candidates = new[] { valid, estimated, human, video, empty, noTranscript, tiny, beginner };
        Assert.Same(valid, Assert.Single(HistoricalDialogueDurationPlanner.EligibleVideos(candidates.AsQueryable(), "intermediate")));
    }

    [Theory]
    [InlineData("en-NZ")]
    [InlineData("zh-HK")]
    [InlineData("yue-CN")]
    [InlineData("zh-TW")]
    public void HistoryQueryTranslatesToPostgresAndIsBounded(string locale)
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused_duration_sql_test").Options);
        var sql = HistoricalDialogueDurationPlanner.QuerySamples(db, locale, "intermediate").ToQueryString();
        Assert.Contains("LIMIT", sql);
        Assert.Contains("300", sql);
        Assert.Contains("audio_duration_measured", sql);
        Assert.Contains("\"Level\" IS NULL", sql);
        Assert.Contains("ORDER BY", sql);
    }

    [Fact]
    public async Task HistoryFailureDoesNotBlockGenerationButCancellationIsPreserved()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var planner = new HistoricalDialogueDurationPlanner(services.GetRequiredService<IServiceScopeFactory>(),
            cache, NullLogger<HistoricalDialogueDurationPlanner>.Instance);
        // Deliberately no DB registration: history is unavailable, as in an isolated test/degraded service.
        var plan = await planner.CreateAsync("en-NZ", 120, "intermediate", "tts", default);
        Assert.Equal(350, plan.TargetUnits);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            planner.CreateAsync("en-NZ", 120, "intermediate", "tts", cancelled.Token));
    }
}

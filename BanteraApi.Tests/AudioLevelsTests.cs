using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Gemini;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BanteraApi.Tests;

public class AudioLevelsTests
{
    [Theory]
    [InlineData(null, "intermediate")]
    [InlineData(" Beginner ", "beginner")]
    [InlineData("INTERMEDIATE", "intermediate")]
    [InlineData("advanced", "advanced")]
    public void NormalizesLevelsAndKeepsOldClientsWorking(string? input, string expected) =>
        Assert.Equal(expected, AudioLevels.ForGeneration(input));

    [Theory]
    [InlineData("all")]
    [InlineData("")]
    [InlineData("native")]
    public void RejectsInvalidGenerationLevels(string input)
    {
        Assert.False(AudioLevels.IsValid(input));
        Assert.Throws<ArgumentException>(() => AudioLevels.ForGeneration(input));
    }

    [Fact]
    public void OldRequestDeserializesWithoutLevel()
    {
        var request = JsonSerializer.Deserialize<GenerateAudioRequest>(
            """{"language":"English","languageCode":"en-US","scenario":"","durationSeconds":60}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(request.Level);
        Assert.Equal("intermediate", AudioLevels.ForGeneration(request.Level));
    }

    [Fact]
    public void FiltersLegacyAndExplicitLevelsBeforePagination()
    {
        var legacy = new UserVideo { IsAiGenerated = true };
        var upload = new UserVideo();
        var beginner = new UserVideo { IsAiGenerated = true, Level = "beginner" };
        var intermediate = new UserVideo { IsAiGenerated = true, Level = "intermediate" };
        var advanced = new UserVideo { IsAiGenerated = true, Level = "advanced" };
        var videos = new[] { beginner, legacy, advanced, upload, intermediate }.AsQueryable();
        Assert.Equal(5, AudioLevels.Filter(videos, null).Count());
        Assert.Equal(new[] { legacy, intermediate }, AudioLevels.Filter(videos, "intermediate").ToArray());
        Assert.Equal(intermediate, AudioLevels.Filter(videos, "intermediate").Skip(1).Take(1).Single());
        Assert.Equal(beginner, AudioLevels.Filter(videos, "beginner").Single());
        Assert.Equal(advanced, AudioLevels.Filter(videos, "advanced").Single());
        Assert.Equal("intermediate", AudioLevels.ForContent(legacy));
        Assert.Null(AudioLevels.ForContent(upload));
        Assert.Equal("advanced", AudioLevels.ForContent(advanced));
    }

    [Fact]
    public void LevelFilterTranslatesToPostgresWithLegacyFallback()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused_level_sql_test").Options);
        var sql = AudioLevels.Filter(db.UserVideos.Where(v => v.IsPublic), "intermediate")
            .OrderByDescending(v => v.CreatedAt).Skip(20).Take(20).ToQueryString();
        Assert.Contains("\"Level\" IS NULL", sql);
        Assert.Contains("\"IsAiGenerated\"", sql);
        Assert.Contains("intermediate", sql);
        Assert.Contains("OFFSET", sql);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    [InlineData("ja-JP")]
    public void BeginnerDurationBudgetAllowsSlowerSpeech(string locale)
    {
        var old = DialogueDurationPlan.Create(locale, 120);
        var beginner = DialogueDurationPlan.Create(locale, 120, "beginner");
        Assert.True(beginner.TargetUnits < old.TargetUnits);
        Assert.Equal(old.TargetUnits, DialogueDurationPlan.Create(locale, 120, "intermediate").TargetUnits);
        Assert.Equal(old.TargetUnits, DialogueDurationPlan.Create(locale, 120, "advanced").TargetUnits);
        Assert.Equal(120, beginner.EstimateSeconds(beginner.TargetUnits));
    }
}

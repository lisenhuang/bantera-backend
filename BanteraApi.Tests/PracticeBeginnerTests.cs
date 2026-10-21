using System.Text.Json;
using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;

public class PracticeBeginnerTests
{
    [Fact]
    public void ExistingPersistedLevelNumbersKeepTheirMeanings()
    {
        Assert.Equal(new[] { "A1", "A2", "B1", "B2", "C1", "C2" },
            Enumerable.Range(0, 6).Select(PracticeRules.LevelName));
        Assert.Equal("A0", PracticeRules.LevelName(-1));
        Assert.Equal(-1, PracticeRules.LevelIndex("A0"));
        Assert.Equal(-2, PracticeRules.LevelIndex("unknown"));
    }

    [Fact]
    public void LegacyRequestsDoNotOptIntoBeginnerExercises()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var next = JsonSerializer.Deserialize<PracticeEndpoints.NextRequest>(
            "{\"requestId\":\"00000000-0000-0000-0000-000000000001\",\"locale\":\"en-US\"}", options)!;
        var level = JsonSerializer.Deserialize<PracticeEndpoints.LevelRequest>("{\"locale\":\"en-US\",\"level\":\"A1\"}", options)!;
        Assert.False(next.SupportsA0); Assert.False(level.SupportsA0);
        Assert.Equal(0, PracticeRules.ClientLevel(-1, false));
        Assert.Equal(-1, PracticeRules.ClientLevel(-1, true));
    }

    [Fact]
    public void BeginnerCanGraduateButCannotSkipAhead()
    {
        var p = new PracticeProgress { Level = -1, Language = "en" };
        Assert.False(PracticeRules.SelectLevel(p, "A1"));
        for (var i = 0; i < 12; i++)
        {
            var e = new PracticeExposure { DeferredScoring = true, Dialogue = new() { Level = -1, Language = "en" } };
            PracticeRules.RecordAnswer(e, p, "correct");
            PracticeRules.Finish(e, p, supportsA0: true);
            PracticeRules.Finish(e, p, supportsA0: true);
        }
        Assert.Equal(0, p.Level); Assert.Empty(p.Window);
    }

    [Theory]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    public void DemotionRespectsClientSupport(bool supportsA0, int expected)
    {
        var p = new PracticeProgress { Level = 0 };
        for (var i = 0; i < 12; i++) PracticeRules.Record(p, false, supportsA0);
        Assert.Equal(expected, p.Level);
        for (var i = 0; i < 24; i++) PracticeRules.Record(p, false, supportsA0);
        Assert.Equal(expected, p.Level);
    }

    [Fact]
    public void ExistingLearnerCanReviewA0WithoutLosingProgress()
    {
        var p = new PracticeProgress { Level = 3, Window = "11111", Language = "en" };
        Assert.True(PracticeRules.SelectLevel(p, "A0"));
        Assert.Equal(-1, PracticeRules.EffectiveLevel(p));
        Assert.Equal(3, PracticeRules.EffectiveLevel(p, false));
        var e = new PracticeExposure { DeferredScoring = true, ReviewMode = true, Dialogue = new() { Level = -1, Language = "en" } };
        PracticeRules.RecordAnswer(e, p, "correct"); PracticeRules.Finish(e, p, true);
        Assert.Equal(3, p.Level); Assert.Equal("11111", p.Window);
        Assert.True(PracticeRules.SelectLevel(p, "B2")); Assert.Null(p.SelectedLevel);
    }

    [Fact]
    public void PrefetchIncludesA0AtModernA1DemotionBoundary()
    {
        var p = new PracticeProgress { Level = 0, Window = "00000000" };
        Assert.Contains(-1, PracticePreparation.Levels(p, supportsA0: true));
        Assert.DoesNotContain(-1, PracticePreparation.Levels(p));
        p.Level = -1; p.Window = "11111111111";
        Assert.Contains(0, PracticePreparation.Levels(p, supportsA0: true));
    }

    [Theory]
    [InlineData("Hello.", "Goodbye.", "hello", "correct")]
    [InlineData("Thank you.", "My name is Anna.", "thank you", "correct")]
    [InlineData("Yes.", "Coffee, please.", "coffee please", "incorrect")]
    public void ShortBeginnerRepliesCanBeRecognised(string reply, string distractor, string heard, string outcome)
    {
        var d = new PracticeDialogue { Locale = "en-US", Reply = reply, Distractor = distractor };
        Assert.Equal(outcome, PracticeAnswerMatcher.Judge(d, false, heard, null).Outcome);
    }
}

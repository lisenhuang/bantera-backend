using BanteraApi.Practice;
using Xunit;
namespace BanteraApi.Tests;

public class PracticeLevelCompletionTests
{
    [Fact] public void NewLearnerHasOnlyA0Available()
    {
        var p = new PracticeProgress { Level = PracticeRules.BeginnerLevel };
        Assert.Equal("practicing", PracticeRules.LevelStates(p)["A0"]);
        Assert.All(PracticeRules.LevelStates(p).Where(x => x.Key != "A0"), x => Assert.Equal("locked", x.Value));
        Assert.False(PracticeRules.SelectLevel(p, "A1"));
    }
    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void EveryLevelIncludingC2RequiresTenOfTwelve(int level)
    {
        var p = new PracticeProgress { Level = level, HighestPassedLevel = level - 1 };
        for (var i = 0; i < 10; i++) PracticeRules.Record(p, true, true);
        PracticeRules.Record(p, false, true);
        Assert.Equal("practicing", PracticeRules.LevelStates(p)[PracticeRules.LevelName(level)]);
        PracticeRules.Record(p, false, true);
        Assert.Equal(level, p.HighestPassedLevel);
        Assert.Equal("passed", PracticeRules.LevelStates(p)[PracticeRules.LevelName(level)]);
        Assert.Equal(Math.Min(5, level + 1), p.Level);
        if (level < 5) Assert.Equal("practicing", PracticeRules.LevelStates(p)[PracticeRules.LevelName(level + 1)]);
    }
    [Fact] public void NineOfTwelveDoesNotPassC2ButNextCorrectCan()
    {
        var p = new PracticeProgress { Level = 5, HighestPassedLevel = 4 };
        for (var i = 0; i < 3; i++) PracticeRules.Record(p, false, true);
        for (var i = 0; i < 9; i++) PracticeRules.Record(p, true, true);
        Assert.Equal("practicing", PracticeRules.LevelStates(p)["C2"]);
        PracticeRules.Record(p, true, true);
        Assert.Equal("passed", PracticeRules.LevelStates(p)["C2"]);
        for (var i = 0; i < 12; i++) PracticeRules.Record(p, false, true);
        Assert.All(PracticeRules.LevelStates(p), x => Assert.Equal("passed", x.Value));
        Assert.True(PracticeRules.SelectLevel(p, "C2"));
    }
    [Theory] [InlineData(true, false)] [InlineData(false, true)]
    public void TranslatedAndReviewExercisesCannotCompleteC2(bool translation, bool review)
    {
        var p = new PracticeProgress { Language = "ja", Level = 5, HighestPassedLevel = 4, Window = "11111111111" };
        var e = new PracticeExposure { DeferredScoring = true, TranslationUsed = translation, ReviewMode = review,
            Dialogue = new() { Language = "ja", Level = 5 } };
        PracticeRules.RecordAnswer(e, p, "correct"); PracticeRules.Finish(e, p, true);
        Assert.Equal(4, p.HighestPassedLevel);
        Assert.Equal("11111111111", p.Window);
    }
    [Fact] public void CompletingAnExerciseTwiceDoesNotScoreTwice()
    {
        var p = new PracticeProgress { Language = "ko", Level = 5, HighestPassedLevel = 4, Window = "1111111111" };
        var e = new PracticeExposure { DeferredScoring = true, Dialogue = new() { Language = "ko", Level = 5 } };
        PracticeRules.RecordAnswer(e, p, "correct"); PracticeRules.Finish(e, p, true); PracticeRules.Finish(e, p, true);
        Assert.Equal(4, p.HighestPassedLevel);
        Assert.Equal(11, p.Window.Length);
    }
}

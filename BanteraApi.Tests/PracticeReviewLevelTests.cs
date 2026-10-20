using BanteraApi.Practice;
using Xunit;
namespace BanteraApi.Tests;

public class PracticeReviewLevelTests
{
    [Fact] public void EasierReviewPreservesEarnedLevelAndWindowAndCanReturn()
    {
        var p = new PracticeProgress { Language = "en", Level = 3, Window = "10111" };
        Assert.True(PracticeRules.SelectLevel(p, "A1"));
        Assert.Equal(0, PracticeRules.EffectiveLevel(p));
        Assert.Equal(3, p.Level); Assert.Equal("10111", p.Window);
        Assert.True(PracticeRules.SelectLevel(p, "B2"));
        Assert.Null(p.SelectedLevel); Assert.Equal(3, PracticeRules.EffectiveLevel(p));
        Assert.Equal("10111", p.Window);
    }
    [Theory] [InlineData("B2")] [InlineData("C1")] [InlineData("C2")] [InlineData("unknown")]
    public void CannotChooseAboveCurrentOrInvalidLevel(string level)
    {
        var p = new PracticeProgress { Level = 2, SelectedLevel = 0, Window = "101" };
        Assert.False(PracticeRules.SelectLevel(p, level));
        Assert.Equal(0, p.SelectedLevel); Assert.Equal("101", p.Window);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ReviewNeverChangesEarnedScoreEvenIfOtherDeviceLaterDropsToReviewLevel(bool deferred)
    {
        var p = new PracticeProgress { Language = "en", Level = 1, Window = "11111111111" };
        var e = new PracticeExposure { ReviewMode = true, DeferredScoring = deferred, Dialogue = new() { Language = "en", Level = 1 } };
        PracticeRules.RecordAnswer(e, p, "correct"); PracticeRules.Finish(e, p);
        Assert.Equal(1, p.Level); Assert.Equal("11111111111", p.Window);
        Assert.True(e.FirstCorrect); Assert.NotNull(e.FinishedAt);
    }
    [Fact] public void DowngradeRelocksHigherLevelsAndOldClientsUseEarnedLevel()
    {
        var p = new PracticeProgress { Level = 3, Window = "00000000000", SelectedLevel = 2 };
        Assert.Equal(3, PracticeRules.EffectiveLevel(p, false));
        PracticeRules.Record(p, false);
        Assert.Equal(2, p.Level); Assert.Null(p.SelectedLevel);
        Assert.False(PracticeRules.SelectLevel(p, "B2"));
    }
    [Fact] public void PrefetchWarmsSelectedReviewWithoutProjectingScoring()
    {
        var p = new PracticeProgress { Language = "en", Level = 3, SelectedLevel = 0, Window = "11111111111" };
        Assert.Equal(new[] { 0 }, PracticePreparation.Levels(p, useSelected: true));
        Assert.Equal(3, p.Level); Assert.Equal("11111111111", p.Window);
    }
}

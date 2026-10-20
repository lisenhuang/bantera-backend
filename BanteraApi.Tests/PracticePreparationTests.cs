using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;
public class PracticePreparationTests
{
    [Fact]
    public void EarlyPracticeOnlyPreparesCurrentLevel()
    {
        var progress = new PracticeProgress { Level = 2, Window = "1111111", Language = "en" };
        Assert.Equal(new[] { 2 }, PracticePreparation.Levels(progress));
    }
    [Theory]
    [InlineData("11111111", 3)]
    [InlineData("00000000", 1)]
    public void PreparesReachableNeighbourFourRepliesBeforeBoundary(string window, int neighbour)
    {
        var progress = new PracticeProgress { Level = 2, Window = window, Language = "en" };
        Assert.Contains(neighbour, PracticePreparation.Levels(progress));
        Assert.Equal(window, progress.Window);
        Assert.Equal(2, progress.Level);
    }
    [Fact]
    public void DeferredCorrectAnswerPreparesUpgradeBeforeNextWithoutScoring()
    {
        var progress = new PracticeProgress { Level = 0, Window = "11111111111", Language = "en" };
        var active = new PracticeExposure { DeferredScoring = true, FirstCorrect = true, Dialogue = new() { Level = 0, Language = "en" } };
        Assert.Equal(new[] { 1, 0 }, PracticePreparation.Levels(progress, active));
        Assert.Equal(0, progress.Level);
        Assert.Null(active.FinishedAt);
        active.TranslationUsed = true;
        Assert.Equal(0, PracticePreparation.Levels(progress, active)[0]);
    }
    [Fact]
    public void PrefetchAlwaysCoversActualNextLevelForScoringAndAssistance()
    {
        for (var level = 0; level < 6; level++)
        for (var length = 0; length <= 12; length++)
        for (var successes = 0; successes <= length; successes++)
        foreach (var deferred in new[] { true, false })
        foreach (var translated in new[] { true, false })
        foreach (bool? correct in new bool?[] { null, true, false })
        {
            var progress = new PracticeProgress { Level = level, Language = "en", Window = new string('1', successes) + new string('0', length-successes) };
            var active = new PracticeExposure { DeferredScoring = deferred, TranslationUsed = translated, FirstCorrect = correct, Dialogue = new() { Level = level, Language = "en" } };
            var candidates = PracticePreparation.Levels(progress, active);
            Assert.All(candidates, n => Assert.InRange(n, 0, 5));
            PracticeRules.Finish(active, progress);
            Assert.Contains(progress.Level, candidates);
        }
    }
}

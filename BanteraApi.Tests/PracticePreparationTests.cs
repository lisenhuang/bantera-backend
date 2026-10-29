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

    [Fact]
    public async Task RefillPreparesTwentyUnseenExercisesAndStopsWhenFull()
    {
        var ready = 3;
        var calls = 0;
        await PracticePreparation.RefillAsync(() => Task.FromResult(ready), () => {
            calls++;
            var produced = Math.Min(5, PracticePreparation.ReadyTarget - ready);
            ready += produced;
            return Task.FromResult(produced);
        }, default);
        Assert.Equal(20, ready);
        Assert.Equal(4, calls);
        await PracticePreparation.RefillAsync(() => Task.FromResult(ready), () => throw new Exception("Full pool must not generate"), default);
    }

    [Fact]
    public async Task RefillCanRecoverFromAnEmptyReviewButStopsRepeatedRejection()
    {
        var ready = 18;
        var calls = 0;
        await PracticePreparation.RefillAsync(() => Task.FromResult(ready), () => {
            var produced = ++calls == 2 ? 2 : 0;
            ready += produced;
            return Task.FromResult(produced);
        }, default);
        Assert.Equal(20, ready);
        calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PracticePreparation.RefillAsync(
            () => Task.FromResult(0), () => { calls++; return Task.FromResult(0); }, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RefillRemainsBoundedWhenUserConsumesTheBuffer()
    {
        var calls = 0;
        // New exercises are consumed as fast as they are produced. This must
        // not be mistaken for repeated generation/review failure.
        await PracticePreparation.RefillAsync(() => Task.FromResult(0), () => {
            calls++; return Task.FromResult(5);
        }, default);
        Assert.Equal(6, calls);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PracticePreparation.RefillAsync(
            () => Task.FromResult(0), () => throw new Exception("Cancelled refill generated"), stop.Token));
    }

    [Fact]
    public void BeginnerVocabularyVariationsAreAllowedButExactPairsStayBlocked()
    {
        var old = new PracticeDialogue { Cue = "Tea?", Reply = "Yes, please.", MeaningKey = "accept drink" };
        old.Fingerprint = PracticeRules.Fingerprint(old.Cue, old.Reply);
        var variation = new PracticeDraft("food", "accept drink", "Coffee?", "Yes, please.", "Goodbye.", "Accept a drink.");
        Assert.False(PracticePreparation.Duplicate(variation, old, -1));
        Assert.True(PracticePreparation.Duplicate(variation, old, 0));
        Assert.True(PracticePreparation.Duplicate(variation with { Cue = "TEA!", Reply = "Yes please" }, old, -1));
    }
}

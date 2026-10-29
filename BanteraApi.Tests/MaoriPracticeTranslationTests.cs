using System.Text.Json;
using BanteraApi.Practice;
using Xunit;
namespace BanteraApi.Tests;
public class MaoriPracticeTranslationTests
{
    [Fact]
    public void OnlyMaoriReceivesEnglishAndChoicesFollowTheSavedOrder()
    {
        var d = new PracticeDialogue { Locale = "mi-NZ", MaoriEnglishJson = JsonSerializer.Serialize(new MaoriEnglishTranslation("Hello?", "Hello.", "Goodbye.")) };
        var e = new PracticeExposure { Dialogue = d };
        Assert.Equal("Hello.", MaoriPracticeTranslation.Present(e)!["option0"]);
        e.Swap = true;
        Assert.Equal("Goodbye.", MaoriPracticeTranslation.Present(e)!["option0"]);
        Assert.NotEmpty(MaoriPracticeTranslation.Instruction("mi-NZ"));
        d.Locale = "ja-JP";
        Assert.Null(MaoriPracticeTranslation.Present(e));
        Assert.Empty(MaoriPracticeTranslation.Instruction(d.Locale));
        Assert.False(MaoriPracticeTranslation.Valid(new("Hello", "", "Goodbye")));
    }
    [Fact]
    public void ListeningReviewNeverAwardsLevelPasses()
    {
        var progress = new PracticeProgress { Language = "mi", Level = -1, HighestPassedLevel = -2 };
        for (var i = 0; i < 12; i++) {
            var e = new PracticeExposure { Dialogue = new() { Language = "mi", Level = -1 }, ReviewMode = true, DeferredScoring = true };
            PracticeRules.RecordAnswer(e, progress, "correct"); PracticeRules.Finish(e, progress, true);
        }
        Assert.Equal(-1, progress.Level); Assert.Equal(-2, progress.HighestPassedLevel); Assert.Empty(progress.Window);
    }
    [Fact]
    public void AlignmentResolvesExactPhrasesAndSwappedOptionsWithoutChangingTranslations()
    {
        var translation = new MaoriEnglishTranslation("Where is the station?", "It is here.", "Tomorrow.", new(
            [new("Kei hea", "Where"), new("te teihana", "the station")],
            [new("Kei konei", "here")], [new("Āpōpō", "Tomorrow")]));
        var d = new PracticeDialogue { Locale = "mi-NZ", Cue = "Kei hea te teihana?", Reply = "Kei konei.", Distractor = "Āpōpō." };
        d.MaoriEnglishJson = JsonSerializer.Serialize(MaoriPracticeTranslation.Prepare(translation, d.Cue, d.Reply, d.Distractor));
        var e = new PracticeExposure { Dialogue = d };
        var ranges = MaoriPracticeTranslation.PresentAlignments(e)!;
        Assert.Equal(new MaoriTranslationRange(0, 7, 0, 5, "Kei hea", "Where"), ranges["cue"][0]);
        Assert.Equal("here", ranges["option0"][0].TargetText);
        e.Swap = true;
        Assert.Equal("Tomorrow", MaoriPracticeTranslation.PresentAlignments(e)!["option0"][0].TargetText);
        Assert.Equal("Tomorrow.", MaoriPracticeTranslation.Present(e)!["option0"]);
        Assert.Equal("ready", MaoriPracticeTranslation.AlignmentStatus(d));
    }

    [Fact]
    public void AlignmentHandlesRepeatedWordsReorderedMeaningAndUtf16Offsets()
    {
        var ranges = MaoriPracticeTranslation.ResolveSentence("😀 te whare, te kura", "the school, the house", [
            new("te whare", "the house"), new("te", "the", 1, 0), new("kura", "school")]);
        Assert.NotNull(ranges);
        Assert.Equal(3, ranges[0].SourceStart);
        Assert.Equal(12, ranges[0].TargetStart);
        Assert.Equal(13, ranges[1].SourceStart);
        Assert.Equal(0, ranges[1].TargetStart);
        Assert.Equal("te", "😀 te whare, te kura".Substring(ranges[1].SourceStart, ranges[1].SourceEnd - ranges[1].SourceStart));
    }

    [Theory]
    [InlineData("ā", "house", 0, 0)]
    [InlineData("whar", "house", 0, 0)]
    [InlineData("whare", "hous", 0, 0)]
    [InlineData("whare", "building", 0, 0)]
    [InlineData("whare", "house", 2, 0)]
    [InlineData("whare", "house", -1, 0)]
    [InlineData(" whare", "house", 0, 0)]
    public void AlignmentRejectsWrongTextPartialWordsAndBadOccurrences(string source, string target, int sourceOccurrence, int targetOccurrence)
        => Assert.Null(MaoriPracticeTranslation.ResolveSentence("te whare", "the house", [new(source, target, sourceOccurrence, targetOccurrence)]));

    [Fact]
    public void LegacyTranslationsRemainReadableAndAlignmentRetriesStop()
    {
        var original = new MaoriEnglishTranslation("Where?", "Here.", "Tomorrow.");
        var d = new PracticeDialogue { Locale = "mi-NZ", Cue = "Kei hea?", Reply = "Kei konei.", Distractor = "Āpōpō.", MaoriEnglishJson = JsonSerializer.Serialize(original) };
        var e = new PracticeExposure { Dialogue = d };
        Assert.Equal("Where?", MaoriPracticeTranslation.Present(e)!["cue"]);
        Assert.Null(MaoriPracticeTranslation.PresentAlignments(e));
        Assert.True(MaoriPracticeTranslation.NeedsAlignment(d));
        d.MaoriEnglishJson = JsonSerializer.Serialize(original with { AlignmentAttempts = MaoriPracticeTranslation.MaximumAlignmentAttempts });
        Assert.False(MaoriPracticeTranslation.NeedsAlignment(d));
        Assert.Equal("unavailable", MaoriPracticeTranslation.AlignmentStatus(d));
        Assert.Equal("Where?", MaoriPracticeTranslation.Present(e)!["cue"]);
        var generated = MaoriPracticeTranslation.Prepare(original with { AlignmentAttempts = 999 }, d.Cue, d.Reply, d.Distractor);
        Assert.Equal(0, generated.AlignmentAttempts);
    }

    [Fact]
    public void LastAlignmentAttemptRemainsPreparingUntilCompletionOrBoundedTimeout()
    {
        var finalAttempt = new MaoriEnglishTranslation("Where?", "Here.", "Tomorrow.",
            AlignmentAttempts: MaoriPracticeTranslation.MaximumAlignmentAttempts,
            AlignmentPendingUntil: DateTime.UtcNow.AddMinutes(10));
        var d = new PracticeDialogue { Locale = "mi-NZ", MaoriEnglishJson = JsonSerializer.Serialize(finalAttempt) };
        Assert.Equal("preparing", MaoriPracticeTranslation.AlignmentStatus(d));
        Assert.False(MaoriPracticeTranslation.NeedsAlignment(d));
        d.MaoriEnglishJson = JsonSerializer.Serialize(finalAttempt with { AlignmentPendingUntil = DateTime.UtcNow.AddSeconds(-1) });
        Assert.Equal("unavailable", MaoriPracticeTranslation.AlignmentStatus(d));
        Assert.False(MaoriPracticeTranslation.NeedsAlignment(d));
        Assert.Null(MaoriPracticeTranslation.Prepare(finalAttempt, "Kei hea?", "Kei konei.", "Āpōpō.").AlignmentPendingUntil);
    }

}

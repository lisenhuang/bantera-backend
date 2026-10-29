using BanteraApi.Practice;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace BanteraApi.Tests;
public class PracticeTests
{
    private static PracticeExposure Deferred() => new() {
        DeferredScoring = true, Dialogue = new() { Language = "en", Level = 0 }
    };
    [Theory]
    [InlineData("correct")]
    [InlineData("incorrect")]
    public void TranslationBeforeOrAfterAnswerExcludesEntireExercise(string outcome)
    {
        foreach (var before in new[] { true, false })
        {
            var p = new PracticeProgress { Language = "en", Window = "11111111111" };
            var e = Deferred();
            e.TranslationUsed = before;
            PracticeRules.RecordAnswer(e, p, outcome);
            Assert.Equal("11111111111", p.Window);
            e.TranslationUsed = true;
            PracticeRules.Finish(e, p);
            Assert.Equal(0, p.Level);
            Assert.Equal("11111111111", p.Window);
        }
    }
    [Fact] public void UnassistedFirstClearAnswerCountsOnceWhenFinished()
    {
        var p = new PracticeProgress { Language = "en", Window = "11111111111" };
        var e = Deferred();
        PracticeRules.RecordAnswer(e, p, "unclear");
        Assert.Null(e.FirstCorrect);
        PracticeRules.RecordAnswer(e, p, "correct");
        PracticeRules.RecordAnswer(e, p, "incorrect");
        Assert.Equal(0, p.Level);
        PracticeRules.Finish(e, p);
        Assert.Equal(1, p.Level);
        PracticeRules.Finish(e, p);
        Assert.Empty(p.Window);
    }
    [Fact] public void WrongFirstAnswerStillCountsAfterCorrectRetryAndSkipsDoNot()
    {
        var p = new PracticeProgress { Language = "en" };
        var e = Deferred();
        PracticeRules.RecordAnswer(e, p, "incorrect");
        PracticeRules.RecordAnswer(e, p, "correct");
        PracticeRules.Finish(e, p);
        Assert.Equal("0", p.Window);
        PracticeRules.Finish(Deferred(), p);
        Assert.Equal("0", p.Window);
    }
    [Fact] public void OldClientsStillScoreImmediatelyAndDoNotScoreTwiceOnNext()
    {
        var p = new PracticeProgress { Language = "en" };
        var e = Deferred(); e.DeferredScoring = false;
        PracticeRules.RecordAnswer(e, p, "correct");
        Assert.Equal("1", p.Window);
        PracticeRules.Finish(e, p);
        Assert.Equal("1", p.Window);
    }
    [Fact] public void FinishedAndDifferentLevelExercisesCannotAddResults()
    {
        var p = new PracticeProgress { Language = "en", Level = 1 };
        var e = Deferred();
        PracticeRules.RecordAnswer(e, p, "correct");
        PracticeRules.Finish(e, p);
        Assert.Empty(p.Window);
        e = Deferred(); PracticeRules.Finish(e, p);
        PracticeRules.RecordAnswer(e, p, "correct");
        Assert.Null(e.FirstCorrect);
    }
    [Fact] public void PunctuationAndCaseDoNotCreateFreshMaterial() =>
        Assert.Equal(PracticeRules.Fingerprint("Where is the station?", "Next to the bank."), PracticeRules.Fingerprint(" WHERE is the station! ", "next to the bank"));
    [Fact] public void MeaningSignatureCatchesRewording()
    {
        var old = new PracticeDialogue { Cue = "Where is the station?", Reply = "Next to the bank.", MeaningKey = "asking location: adjacent landmark" };
        var candidate = new PracticeDraft("transport", "Asking location - adjacent landmark", "Where can I find the market?", "Beside the library.", "Tomorrow.", "A place is requested.");
        Assert.True(PracticeRules.Duplicate(candidate, old));
    }
    [Fact] public void VocabularyReuseInDifferentSituationsIsAllowed()
    {
        var old = new PracticeDialogue { Cue = "Where is the station?", Reply = "Near the bank.", MeaningKey = "request location landmark" };
        Assert.False(PracticeRules.Duplicate(new("transport", "request opening time", "When does the station open?", "At six.", "On the left.", "A time is requested."), old));
    }
    [Fact] public void OneAnswerCannotChangeDifficultyAndRepeatedWindowsAreNeeded()
    {
        var p = new PracticeProgress(); PracticeRules.Record(p, true); Assert.Equal(0, p.Level);
        for (var i=0;i<11;i++) PracticeRules.Record(p, true);
        Assert.Equal(1, p.Level); Assert.Empty(p.Window);
        for (var i=0;i<11;i++) PracticeRules.Record(p, false);
        Assert.Equal(1,p.Level); PracticeRules.Record(p,false); Assert.Equal(0,p.Level);
    }
    [Fact] public void LevelBoundsAndWindowSizeAreEnforced()
    {
        var p = new PracticeProgress { Level = 5 };
        for(var i=0;i<100;i++) PracticeRules.Record(p,true);
        Assert.Equal(5,p.Level); Assert.Equal(12,p.Window.Length);
        p.Level=0; p.Window="";
        for(var i=0;i<100;i++) PracticeRules.Record(p,false);
        Assert.Equal(0,p.Level); Assert.Equal(12,p.Window.Length);
    }
    [Fact] public void SchemaAllowsReplaysButKeepsRequestsAndFingerprintsUnique()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        var exposure = db.Model.FindEntityType(typeof(PracticeExposure))!;
        Assert.Contains(exposure.GetIndexes(), i => !i.IsUnique && i.Properties.Select(p=>p.Name).SequenceEqual(new[]{"UserId","DialogueId"}));
        Assert.Contains(exposure.GetIndexes(), i => i.IsUnique && i.Properties.Select(p=>p.Name).SequenceEqual(new[]{"UserId","RequestId"}));
        var dialogue = db.Model.FindEntityType(typeof(PracticeDialogue))!;
        Assert.Contains(dialogue.GetIndexes(), i => i.IsUnique && i.Properties.Select(p=>p.Name).SequenceEqual(new[]{"Language","Fingerprint"}));
    }
}

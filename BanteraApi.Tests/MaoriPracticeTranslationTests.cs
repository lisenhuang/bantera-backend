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
}

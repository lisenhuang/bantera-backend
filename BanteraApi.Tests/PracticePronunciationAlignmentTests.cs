using System.Text.Json;
using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;

public class PracticePronunciationAlignmentTests
{
    [Fact]
    public void SharedLanguageFixturesEnforceAlignment()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/pronunciation_alignment.json")));
        foreach (var row in fixture.RootElement.EnumerateArray())
        {
            var locale = row.GetProperty("locale").GetString()!;
            var text = row.GetProperty("text").GetString()!;
            var parts = row.GetProperty("segments").Deserialize<PronunciationSegment[]>(PracticePronunciation.Json);
            Assert.True(PracticePronunciation.ValidSegments(parts, text, locale) == row.GetProperty("valid").GetBoolean(), $"{locale}: {text}");
        }
    }

    [Fact]
    public void RepairIsResumablePreservesPracticeAndDoesNotResetBudgetAgain()
    {
        var data = new PracticePronunciationData([new("朋友", "péng you")], [new("你", "nǐ")], [new("好", "hǎo")]);
        var d = new PracticeDialogue { Locale = "zh-CN", Cue = "朋友", Reply = "你", Distractor = "好",
            PronunciationJson = JsonSerializer.Serialize(data, PracticePronunciation.Json), PronunciationAttempts = 5,
            CueAudio = "original-audio", Ready = true, Fingerprint = "unchanged" };
        var now = DateTime.UtcNow;
        Assert.Null(PracticePronunciation.Read(d));
        Assert.True(PracticePronunciationRepair.Inspect(d, now));
        Assert.Null(d.PronunciationJson);
        Assert.Equal(0, d.PronunciationAttempts);
        Assert.Equal(now, d.PronunciationRequestedAt);
        Assert.Equal("朋友", d.Cue);
        Assert.Equal("original-audio", d.CueAudio);
        Assert.Equal("unchanged", d.Fingerprint);
        Assert.True(d.Ready);
        d.PronunciationAttempts = 5;
        Assert.False(PracticePronunciationRepair.Inspect(d, now.AddDays(1)));
        Assert.Equal(5, d.PronunciationAttempts);
    }

    [Fact]
    public void CorrectLegacyDataIsMarkedWithoutRegenerationOrJsonRewrite()
    {
        var parts = new[] { new PronunciationSegment("你", "nǐ") };
        var json = JsonSerializer.Serialize(new PracticePronunciationData(parts, parts, parts), PracticePronunciation.Json);
        var d = new PracticeDialogue { Locale = "zh-CN", Cue = "你", Reply = "你", Distractor = "你", PronunciationJson = json };
        Assert.False(PracticePronunciationRepair.Inspect(d, DateTime.UtcNow));
        Assert.Equal(json, d.PronunciationJson);
        Assert.Equal(PracticePronunciation.AlignmentVersion, d.PronunciationAlignmentVersion);
        Assert.Null(d.PronunciationRequestedAt);
    }
}

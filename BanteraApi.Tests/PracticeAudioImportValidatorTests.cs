using BanteraApi.Mcp;
using BanteraApi.Videos;
using ModelContextProtocol;
using Xunit;

namespace BanteraApi.Tests;

public class PracticeAudioImportValidatorTests
{
    private static readonly string[] Lines = ["Hello there.", "How are you?"];
    private static readonly VideoTranscriptCue[] Cues =
    [
        new(0, 0, 1000, "Hello there."),
        new(1, 1100, 2300, "How are you?"),
    ];
    private static readonly WordTimingRecord[] Words =
    [
        new("Hello", 100, 420, .99),
        new("there", 500, 900, .98),
        new("How", 1200, 1400, .98),
        new("are", 1450, 1600, .98),
        new("you", 1700, 2100, .98),
    ];

    [Fact]
    public void ValidLessonDerivesTranscriptFromCues()
    {
        var transcript = PracticeAudioImportValidator.Validate("A greeting", "English", "en-US",
            "Hello there.\nHow are you?", 2500, Lines, Cues, null, Words);

        Assert.Equal("Hello there.\nHow are you?", transcript);
    }

    [Fact]
    public void RejectsDialogueThatDoesNotMatchPlaybackCues()
    {
        Assert.Throws<McpException>(() => PracticeAudioImportValidator.Validate("A greeting", "English", "en-US",
            "Hello there.\nHow are you?", 2500, ["Different", Lines[1]], Cues, null, Words));
    }

    [Fact]
    public void RejectsShortCuesThatDropWords()
    {
        VideoTranscriptCue[] shortCues = [new(0, 0, 1000, "Hello there."), new(1, 1100, 2300, "How are?")];

        Assert.Throws<McpException>(() => PracticeAudioImportValidator.Validate("A greeting", "English", "en-US",
            "Hello there.\nHow are you?", 2500, Lines, Cues, shortCues, Words));
    }

    [Fact]
    public void RejectsWordTimingBeyondAudioDuration()
    {
        var words = Words.ToArray();
        words[^1] = new WordTimingRecord("you", 1700, 3000, .98);

        Assert.Throws<McpException>(() => PracticeAudioImportValidator.Validate("A greeting", "English", "en-US",
            "Hello there.\nHow are you?", 2500, Lines, Cues, null, words));
    }

    [Fact]
    public void RejectsTranscriptThatWasNotSuppliedConsistently()
    {
        Assert.Throws<McpException>(() => PracticeAudioImportValidator.Validate("A greeting", "English", "en-US",
            "Hello there.\nHow are we?", 2500, Lines, Cues, null, Words));
    }

    [Fact]
    public void TranscriptUpdateMayChangeLineBreaksButNotSpokenText()
    {
        Assert.True(PracticeAudioImportValidator.HasSameSpokenText(
            "Hello there. How are you?", "Hello there.\nHow are you?"));
        Assert.False(PracticeAudioImportValidator.HasSameSpokenText(
            "Hello there. How are you?", "Hello there.\nHow are we?"));
    }
}

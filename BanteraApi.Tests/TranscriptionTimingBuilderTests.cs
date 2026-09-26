using BanteraApi.Gemini;
using Xunit;

namespace BanteraApi.Tests;

public class TranscriptionTimingBuilderTests
{
    [Fact]
    public void UsesTranscribedTextAndTimesEvenWhenScriptWouldDiffer()
    {
        var result = TranscriptionTimingBuilder.Build(
        [
            new("I", 100, 250, "Speaker1"),
            new("heard", 260, 600, "Speaker1"),
            new("seven.", 610, 950, "Speaker1"),
        ], 1200);

        Assert.NotNull(result);
        Assert.Equal("geminiTranscribeDirect", result.Mode);
        Assert.Equal("I heard seven.", result.Cues[0].Text);
        Assert.Equal(result.Cues, result.ShortCues);
        Assert.Equal(result.Cues[0].Text, result.DisplayLines[0].Text);
        Assert.Equal(["I", "heard", "seven"], result.WordTiming.Select(w => w.Word));
        Assert.Equal([100, 260, 610], result.WordTiming.Select(w => w.StartMs));
    }

    [Fact]
    public void KeepsCjkTextAndCharacterTimingsCompatibleWithPlayer()
    {
        var result = TranscriptionTimingBuilder.Build(
        [new("你好", 100, 500, "Speaker1"), new("世界。", 510, 900, "Speaker1")], 1000);

        Assert.NotNull(result);
        Assert.Equal("你好世界。", result.Cues[0].Text);
        Assert.Equal(["你好", "世界"], result.WordTiming.Select(w => w.Word));
        Assert.Equal([100, 510], result.WordTiming.Select(w => w.StartMs));
        Assert.Equal(["你", "好"], result.WordTiming[0].Parts!.Select(p => p.Word));
        Assert.Equal([100, 300], result.WordTiming[0].Parts!.Select(p => p.StartMs));
    }

    [Fact]
    public void RepairsZeroLengthChineseWordWithoutDiscardingOtherTiming()
    {
        TranscribedWord[] words = [new("你好", 100, 100), new("世界", 250, 650)];

        var result = TranscriptionTimingBuilder.Build(words, 800, out var issue, out var repairedWords);

        Assert.NotNull(result);
        Assert.Null(issue);
        Assert.Equal(1, repairedWords);
        Assert.Equal(100, result.WordTiming[0].StartMs);
        Assert.Equal(250, result.WordTiming[0].EndMs);
        Assert.Equal(0.0, result.WordTiming[0].Confidence);
        Assert.Equal(250, result.WordTiming[1].StartMs);
        Assert.Equal(650, result.WordTiming[1].EndMs);
        Assert.Equal(1.0, result.WordTiming[1].Confidence);
        Assert.Equal(["你", "好"], result.WordTiming[0].Parts!.Select(p => p.Word));
        Assert.Equal(["你", "好", "世", "界"], result.Cues.SelectMany(c => WordTimingAligner.Tokenize([c.Text])).Select(t => t.Text));
        Assert.Equal(100, words[0].EndMs); // The provider response is left untouched.
    }

    [Fact]
    public void KeepsDirectTimingWhenSeveralChineseWordsHaveZeroLength()
    {
        var result = TranscriptionTimingBuilder.Build(
            [new("我", 100, 100), new("们", 100, 100), new("好", 200, 200), new("世界", 300, 600)],
            800, out var issue, out var repairedWords);

        Assert.NotNull(result);
        Assert.Null(issue);
        Assert.Equal(3, repairedWords);
        Assert.Equal("我们好世界", result.Cues[0].Text);
        Assert.All(result.WordTiming, word => Assert.True(word.EndMs > word.StartMs));
    }

    [Theory]
    [InlineData(100, 50, 1000, "non_positive_word_duration")]
    [InlineData(100, 2200, 1000, "word_past_audio_end")]
    [InlineData(-1, 100, 1000, "negative_word_start")]
    public void ExplainsInvalidTranscriptionTiming(int start, int end, int duration, string code)
    {
        var result = TranscriptionTimingBuilder.Build([new("hello", start, end)], duration, out var issue);

        Assert.Null(result);
        Assert.Equal(code, issue?.Code);
        Assert.Equal(0, issue?.WordIndex);
        Assert.Equal(start, issue?.StartMs);
        Assert.Equal(end, issue?.EndMs);
    }

    [Fact]
    public void SplitsSpeakerTurnsWithoutUsingOriginalDialogueLines()
    {
        var result = TranscriptionTimingBuilder.Build(
        [new("Hi.", 100, 400, "Speaker1"), new("Hello.", 600, 950, "Speaker2")], 1100);

        Assert.NotNull(result);
        Assert.Equal(["Hi.", "Hello."], result.Cues.Select(c => c.Text));
        Assert.Equal(["Speaker1", "Speaker2"], result.DisplayLines.Select(l => l.Speaker));
        Assert.Equal(["Hi", "Hello"], result.WordTiming.Select(w => w.Word));
    }
}

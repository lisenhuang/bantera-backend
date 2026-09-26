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

    [Fact]
    public void LongEnglishSentenceWaitsForPunctuationDespiteWordLimitAndPause()
    {
        const string sentence = "They also do a grilled fish with sambal and lime that gets great reviews, or we could get the salt and pepper tofu.";
        var words = sentence.Split(' ').Select((text, i) =>
            new TranscribedWord(text, i * 100 + (i >= 18 ? 800 : 0),
                i * 100 + (i >= 18 ? 800 : 0) + 80, "Speaker1")).ToArray();

        var result = TranscriptionTimingBuilder.Build(words, words[^1].EndMs + 200);

        Assert.NotNull(result);
        Assert.Equal([
            "They also do a grilled fish with sambal and lime that gets great reviews,",
            "or we could get the salt and pepper tofu.",
        ], result.Cues.Select(c => c.Text));
        Assert.Equal(result.Cues, result.ShortCues);
    }

    [Fact]
    public void EnglishSentenceBoundaryAvoidsThePublishedMidSentenceSplit()
    {
        const string sentence = "That makes complete sense. In that case, do you want to share a few smaller plates instead of getting separate mains?";
        var words = sentence.Split(' ').Select((text, i) =>
            new TranscribedWord(text, i * 100, i * 100 + 80, "Speaker1")).ToArray();

        var result = TranscriptionTimingBuilder.Build(words, words[^1].EndMs + 200);

        Assert.NotNull(result);
        Assert.Equal([
            "That makes complete sense.",
            "In that case, do you want to share a few smaller plates instead of getting separate mains?",
        ], result.Cues.Select(c => c.Text));
    }

    [Fact]
    public void EnglishWithoutPunctuationStaysInOneCueUntilSpeakerChanges()
    {
        var words = Enumerable.Range(0, 22)
            .Select(i => new TranscribedWord($"word{i}", i * 100 + (i >= 18 ? 800 : 0),
                i * 100 + (i >= 18 ? 800 : 0) + 80, "Speaker1"))
            .Append(new TranscribedWord("Okay.", 3100, 3400, "Speaker2"))
            .ToArray();

        var result = TranscriptionTimingBuilder.Build(words, 3600);

        Assert.NotNull(result);
        Assert.Equal(2, result.Cues.Count);
        Assert.Equal(string.Join(" ", words.Take(22).Select(w => w.Text)), result.Cues[0].Text);
        Assert.Equal("Okay.", result.Cues[1].Text);
    }

    [Fact]
    public void ChineseCuesWaitForCommaInsteadOfCuttingAfterEighteenCharacters()
    {
        const string first = "我们今天下午准备先去市中心的图书馆看书，";
        const string second = "然后再去吃晚饭。";
        var words = (first + second).Select((ch, i) => new TranscribedWord(ch.ToString(), i * 100, (i + 1) * 100)).ToArray();

        var result = TranscriptionTimingBuilder.Build(words, words.Length * 100 + 200);

        Assert.True(first.Length > 18);
        Assert.NotNull(result);
        Assert.Equal([first, second], result.Cues.Select(c => c.Text));
        Assert.Equal(result.Cues.Select(c => c.Text), result.DisplayLines.Select(line => line.Text));
    }

    [Fact]
    public void JapaneseAndKoreanCuesAlsoSplitAtPunctuation()
    {
        var japanese = "今日は図書館で本を読んで、それから食事に行きます。";
        var japaneseWords = japanese.Select((ch, i) => new TranscribedWord(ch.ToString(), i * 100, (i + 1) * 100)).ToArray();
        var japaneseResult = TranscriptionTimingBuilder.Build(japaneseWords, japaneseWords.Length * 100 + 200);
        var koreanResult = TranscriptionTimingBuilder.Build(
            [new("오늘은", 0, 300), new("도서관에", 300, 700), new("갑니다,", 700, 1100),
             new("그다음", 1100, 1500), new("밥을", 1500, 1800), new("먹어요.", 1800, 2200)], 2400);

        Assert.NotNull(japaneseResult);
        Assert.Equal(["今日は図書館で本を読んで、", "それから食事に行きます。"], japaneseResult.Cues.Select(c => c.Text));
        Assert.NotNull(koreanResult);
        Assert.Equal(["오늘은 도서관에 갑니다,", "그다음 밥을 먹어요."], koreanResult.Cues.Select(c => c.Text));
    }

    [Fact]
    public void SplitsPunctuationInsideOneTranscribedChineseWord()
    {
        var result = TranscriptionTimingBuilder.Build([new("你好，世界。再见", 100, 900)], 1000);

        Assert.NotNull(result);
        Assert.Equal(["你好，", "世界。", "再见"], result.Cues.Select(c => c.Text));
        Assert.Equal("你好，世界。再见", string.Concat(result.Cues.Select(c => c.Text)));
        Assert.All(result.WordTiming, word => Assert.True(word.EndMs > word.StartMs));
    }

    [Fact]
    public void KeepsStandalonePunctuationWithItsChineseCueDespiteTimestampGap()
    {
        var result = TranscriptionTimingBuilder.Build(
            [new("你好", 100, 300, "A"), new("，", 1200, 1200, "B"), new("世界。", 1300, 1600, "A")], 1800);

        Assert.NotNull(result);
        Assert.Equal(["你好，", "世界。"], result.Cues.Select(c => c.Text));
    }
}

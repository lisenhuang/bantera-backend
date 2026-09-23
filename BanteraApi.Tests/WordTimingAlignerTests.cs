using System.Text.Json;
using BanteraApi.Gemini;
using Xunit;

namespace BanteraApi.Tests;

public class WordTimingAlignerTests
{
    private static List<TranscribedWord> Words(params (string Text, int Start, int End)[] words) =>
        words.Select(w => new TranscribedWord(w.Text, w.Start, w.End)).ToList();

    [Fact]
    public void Tokenize_MatchesTheAppWordPattern()
    {
        var tokens = WordTimingAligner.Tokenize(["Hey! Don't be late, it's 7:30.", "你好，今天去喝咖啡吧。"]);

        Assert.Equal(["Hey", "Don't", "be", "late", "it's", "7", "30", "你好", "今天去喝咖啡吧"], tokens.Select(t => t.Text));
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 1, 1], tokens.Select(t => t.Line));
    }

    [Fact]
    public void ExactTranscript_TimesEveryWordFromItsTranscriptWord()
    {
        var tokens = WordTimingAligner.Tokenize(["Did you get tickets?"]);
        var words = Words(("Did", 100, 300), ("you", 300, 400), ("get", 400, 600), ("tickets?", 600, 1100));

        var matches = WordTimingAligner.AlignByCharacters(tokens, words);
        var alignment = WordTimingAligner.Resolve(tokens, words, matches, 1500);

        Assert.Equal(4, alignment.Exact);
        Assert.Equal([(100, 300), (300, 400), (400, 600), (600, 1100)], alignment.Tokens.Select(t => (t.StartMs, t.EndMs)));
    }

    [Fact]
    public void Merge_UsesAiOnlyForWordsHeardDifferently()
    {
        // Script says "seven", transcript heard "7:00".
        var tokens = WordTimingAligner.Tokenize(["around seven"]);
        var words = Words(("around", 0, 300), ("7:00.", 300, 700));
        var algorithm = WordTimingAligner.AlignByCharacters(tokens, words);
        TokenMatch?[] ai = [new TokenMatch(1, 1), new TokenMatch(1, 1)]; // AI wrongly maps "around" too

        var merged = WordTimingAligner.Merge(tokens, words, ai, algorithm);
        var alignment = WordTimingAligner.Resolve(tokens, words, merged, 700);

        Assert.Equal(new TokenMatch(0, 0), merged[0]); // exact anchor kept, AI ignored
        Assert.Equal(new TokenMatch(1, 1), merged[1]);
        Assert.Equal(TokenTimingStatus.Exact, alignment.Tokens[0].Status);
        Assert.Equal(TokenTimingStatus.Corrected, alignment.Tokens[1].Status);
        Assert.Equal((300, 700), (alignment.Tokens[1].StartMs, alignment.Tokens[1].EndMs));
    }

    [Fact]
    public void ChineseCharacters_AreGroupedIntoTheScriptsWords()
    {
        var tokens = WordTimingAligner.Tokenize(["你好，喝咖啡吧。"]);
        var words = Words(("你", 300, 400), ("好，", 400, 900), ("喝", 1000, 1100), ("咖", 1100, 1300), ("啡", 1300, 1500), ("吧。", 1500, 1600));

        var alignment = WordTimingAligner.Resolve(tokens, words, WordTimingAligner.AlignByCharacters(tokens, words), 2000);

        Assert.Equal(["你好", "喝咖啡吧"], alignment.Tokens.Select(t => t.Text));
        Assert.Equal([(300, 900), (1000, 1600)], alignment.Tokens.Select(t => (t.StartMs, t.EndMs)));
        Assert.Equal(2, alignment.Exact);
    }

    [Fact]
    public void Resolve_DropsBackwardsMatchesAndEstimatesTheGap()
    {
        var tokens = WordTimingAligner.Tokenize(["one two three"]);
        var words = Words(("one", 0, 200), ("two", 200, 400), ("three", 400, 600));
        TokenMatch?[] matches = [new TokenMatch(0, 0), new TokenMatch(2, 2), new TokenMatch(1, 1)]; // "three" goes backwards

        var alignment = WordTimingAligner.Resolve(tokens, words, matches, 600);

        Assert.Equal(TokenTimingStatus.Estimated, alignment.Tokens[2].Status);
        Assert.True(alignment.Tokens[2].StartMs >= alignment.Tokens[1].StartMs);
        Assert.All(alignment.Tokens, t => Assert.True(t.EndMs > t.StartMs));
    }

    [Fact]
    public void Cues_FollowLinesAndShortCuesWithoutOverlap()
    {
        DialogueLine[] lines =
        [
            new("Speaker1", "Hi there. How are you?") { ShortCues = ["Hi there.", "How are you?"] },
            new("Speaker2", "Great!"),
        ];
        var tokens = WordTimingAligner.Tokenize(lines.Select(l => l.Text).ToArray());
        var words = Words(("Hi", 0, 200), ("there.", 200, 500), ("How", 700, 800), ("are", 800, 900), ("you?", 900, 1200), ("Great!", 1250, 1700));
        var alignment = WordTimingAligner.Resolve(tokens, words, WordTimingAligner.AlignByCharacters(tokens, words), 2000);

        var cues = WordTimingAligner.BuildLineCues(lines.Select(l => l.Text).ToArray(), alignment, 2000)!;
        var shortCues = WordTimingAligner.BuildShortCues(lines, alignment, 2000)!;

        Assert.Equal([(0, 1250), (1250, 1850)], cues.Select(c => (c.StartMs, c.EndMs))); // end padded, capped at next start
        Assert.Equal(["Hi there.", "How are you?", "Great!"], shortCues.Select(c => c.Text));
        Assert.Equal([(0, 650), (700, 1250), (1250, 1850)], shortCues.Select(c => (c.StartMs, c.EndMs)));
    }

    [Fact]
    public void ParseTranscribedWords_ReadsInteractionsWordInfo()
    {
        using var doc = JsonDocument.Parse("""
        {"status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"Hello from Bantera.",
          "annotations":[
            {"type":"word_info","text":"Hello","start_offset":"0.300s","end_offset":"1s","speaker":"spk:0"},
            {"type":"word_info","text":"from","start_offset":"1s","end_offset":"1.300s","speaker":"spk:0"}]}]}]}
        """);

        var words = GeminiService.ParseTranscribedWords(doc.RootElement);

        Assert.Equal([("Hello", 300, 1000), ("from", 1000, 1300)], words.Select(w => (w.Text, w.StartMs, w.EndMs)));
        Assert.Equal("spk:0", words[0].Speaker);
    }

    [Fact]
    public void SelectKeys_WebSearchUsesOnlyPrefixedKeys()
    {
        string[] keys = ["AIzaSyAAA111", "AQ.other222", "AIzaSyBBB333", " ", "AIzaSyAAA111"];

        var search = GeminiService.SelectKeys(keys, webSearch: true, "AIzaSy");
        var normal = GeminiService.SelectKeys(keys, webSearch: false, "AIzaSy");

        Assert.Equal(["AIzaSyAAA111", "AIzaSyBBB333"], search.Order());
        Assert.Equal(["AIzaSyAAA111", "AIzaSyBBB333", "AQ.other222"], normal.Order());
    }

    [Fact]
    public void SelectKeys_WebSearchFallsBackToAllKeysWhenNoneHaveThePrefix()
    {
        Assert.Equal(["k1", "k2"], GeminiService.SelectKeys(["k1", "k2"], webSearch: true, "AIzaSy").Order());
    }
}

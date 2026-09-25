using System.Text;

namespace BanteraApi.Gemini;

/// <summary>Builds displayed cues and word timing from the transcription alone.</summary>
public static class TranscriptionTimingBuilder
{
    private const int MaxWordsPerCue = 18;
    private const int PauseBoundaryMs = 700;

    public static AiAudioTimingResult? Build(IReadOnlyList<TranscribedWord> words, int durationMs)
    {
        if (words.Count == 0 || durationMs <= 0) return null;
        var lexicalWords = words.Where(w => WordTimingAligner.Tokenize([w.Text]).Count > 0).ToList();
        if (lexicalWords.Count == 0) return null;

        // A missing or malformed timestamp cannot highlight the spoken word accurately.
        var previousStart = -1;
        foreach (var word in lexicalWords)
        {
            if (word.StartMs < previousStart || word.StartMs < 0 || word.EndMs <= word.StartMs
                || word.EndMs > durationMs + 1000)
                return null;
            previousStart = word.StartMs;
        }

        var groups = new List<List<TranscribedWord>>();
        var current = new List<TranscribedWord>();
        foreach (var word in words)
        {
            if (current.Count > 0 && ShouldSplit(current, word))
            {
                groups.Add(current);
                current = [];
            }
            current.Add(word);
        }
        if (current.Count > 0) groups.Add(current);

        var lines = new List<DialogueLine>();
        var tokens = new List<TimedToken>();
        var nextWord = 0;
        foreach (var group in groups)
        {
            var text = JoinWords(group);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var line = lines.Count;
            lines.Add(new DialogueLine(group[0].Speaker ?? "Speaker1", text));
            foreach (var word in group)
            {
                var pieces = WordTimingAligner.Tokenize([word.Text]);
                if (pieces.Count == 0) continue;
                var total = pieces.Sum(t => Math.Max(1, t.Key.Length));
                var used = 0;
                var localWord = -1;
                var globalWord = nextWord;
                foreach (var piece in pieces)
                {
                    if (piece.Word != localWord)
                    {
                        localWord = piece.Word;
                        globalWord = nextWord++;
                    }
                    var start = word.StartMs + (int)Math.Round((double)(word.EndMs - word.StartMs) * used / total);
                    used += Math.Max(1, piece.Key.Length);
                    var end = word.StartMs + (int)Math.Round((double)(word.EndMs - word.StartMs) * used / total);
                    tokens.Add(new TimedToken(line, piece.Text, start, Math.Max(start + 1, end), TokenTimingStatus.Exact, globalWord));
                }
            }
        }

        if (lines.Count == 0 || tokens.Count == 0) return null;
        var alignment = new WordAlignment(tokens, tokens.Count, 0, 0, 0);
        var cues = WordTimingAligner.BuildLineCues(lines.Select(l => l.Text).ToArray(), alignment, durationMs);
        if (cues is null) return null;
        // These cues are already short transcript segments and match the timed words exactly.
        return new AiAudioTimingResult(
            WordTimingAligner.ToWordTiming(alignment), cues, cues,
            "geminiTranscribeDirect", alignment, lines.ToArray());
    }

    private static bool ShouldSplit(IReadOnlyList<TranscribedWord> current, TranscribedWord next)
    {
        var last = current[^1];
        if (current.Count >= MaxWordsPerCue || next.StartMs - last.EndMs >= PauseBoundaryMs)
            return true;
        if (!string.IsNullOrWhiteSpace(last.Speaker) && !string.IsNullOrWhiteSpace(next.Speaker)
            && last.Speaker != next.Speaker)
            return true;
        var text = last.Text.TrimEnd();
        return current.Count >= 5 && text.Length > 0 && ".?!。？！".Contains(text[^1]);
    }

    private static string JoinWords(IReadOnlyList<TranscribedWord> words)
    {
        var text = new StringBuilder();
        foreach (var word in words)
        {
            var part = word.Text.Trim();
            if (part.Length == 0) continue;
            if (text.Length > 0 && !IsClosingPunctuation(part[0])
                && !IsCjk(text.ToString().EnumerateRunes().Last())
                && !IsCjk(part.EnumerateRunes().First()))
                text.Append(' ');
            text.Append(part);
        }
        return text.ToString();
    }

    private static bool IsClosingPunctuation(char ch) => ",.!?;:%)]}。，！？；：、」』）".Contains(ch);
    private static bool IsCjk(Rune rune) => WordTimingAligner.IsCjk(rune);
}

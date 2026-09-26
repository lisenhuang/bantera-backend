using System.Text;

namespace BanteraApi.Gemini;

public sealed record TranscriptionTimingIssue(string Code, int? WordIndex = null, int? StartMs = null, int? EndMs = null);

/// <summary>Builds displayed cues and word timing from the transcription alone.</summary>
public static class TranscriptionTimingBuilder
{
    private const int MaxWordsPerCue = 18;
    private const int PauseBoundaryMs = 700;
    private const int MinEstimatedWordMs = 80;
    private const int MaxEstimatedWordMs = 250;
    private const int DefaultEstimatedWordMs = 120;
    private const string CuePunctuation = ",.!?;:，。！？；：、";
    private const string ClosingQuotes = "\"'”’」』）】";

    public static AiAudioTimingResult? Build(IReadOnlyList<TranscribedWord> words, int durationMs)
        => Build(words, durationMs, out _);

    public static AiAudioTimingResult? Build(
        IReadOnlyList<TranscribedWord> words, int durationMs, out TranscriptionTimingIssue? issue)
        => Build(words, durationMs, out issue, out _);

    public static AiAudioTimingResult? Build(
        IReadOnlyList<TranscribedWord> words, int durationMs,
        out TranscriptionTimingIssue? issue, out int repairedWords)
    {
        issue = null;
        repairedWords = 0;
        if (durationMs <= 0) { issue = new("invalid_audio_duration"); return null; }
        if (words.Count == 0) { issue = new("no_words"); return null; }
        var normalizedWords = words.ToArray();
        var lexicalIndices = Enumerable.Range(0, words.Count)
            .Where(i => WordTimingAligner.Tokenize([words[i].Text]).Count > 0)
            .ToArray();
        if (lexicalIndices.Length == 0) { issue = new("no_lexical_words"); return null; }
        var repaired = new HashSet<TranscribedWord>(ReferenceEqualityComparer.Instance);

        // Gemini occasionally gives an otherwise usable Chinese word a zero-length span.
        // Estimate just that word's duration instead of discarding every word's timing.
        var previousStart = -1;
        for (var i = 0; i < lexicalIndices.Length; i++)
        {
            var originalIndex = lexicalIndices[i];
            var word = normalizedWords[originalIndex];
            var code = word.StartMs < 0 ? "negative_word_start"
                : word.StartMs < previousStart ? "timestamps_out_of_order"
                : word.EndMs < word.StartMs ? "non_positive_word_duration"
                : word.EndMs > (long)durationMs + 1000 ? "word_past_audio_end" : null;
            if (code is not null) { issue = new(code, i, word.StartMs, word.EndMs); return null; }
            if (word.EndMs == word.StartMs)
            {
                if (word.StartMs >= durationMs)
                {
                    issue = new("non_positive_word_duration", i, word.StartMs, word.EndMs);
                    return null;
                }
                var nextStart = i + 1 < lexicalIndices.Length
                    ? normalizedWords[lexicalIndices[i + 1]].StartMs : word.StartMs;
                var span = nextStart > word.StartMs
                    ? Math.Clamp(nextStart - word.StartMs, MinEstimatedWordMs, MaxEstimatedWordMs)
                    : DefaultEstimatedWordMs;
                var estimated = word with { EndMs = Math.Min(durationMs, word.StartMs + span) };
                normalizedWords[originalIndex] = estimated;
                repaired.Add(estimated);
                repairedWords++;
            }
            previousStart = word.StartMs;
        }

        var cueWords = new List<TranscribedWord>(normalizedWords.Length);
        var repairedCueWords = new HashSet<TranscribedWord>(ReferenceEqualityComparer.Instance);
        foreach (var word in normalizedWords)
        {
            foreach (var fragment in SplitAtCjkPunctuation(word))
            {
                cueWords.Add(fragment);
                if (repaired.Contains(word)) repairedCueWords.Add(fragment);
            }
        }

        var groups = new List<List<TranscribedWord>>();
        var current = new List<TranscribedWord>();
        foreach (var word in cueWords)
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
                    tokens.Add(new TimedToken(line, piece.Text, start, Math.Max(start + 1, end),
                        repairedCueWords.Contains(word) ? TokenTimingStatus.Estimated : TokenTimingStatus.Exact, globalWord));
                }
            }
        }

        if (lines.Count == 0 || tokens.Count == 0) { issue = new("empty_transcript_cue"); return null; }
        var estimatedTokens = tokens.Count(t => t.Status == TokenTimingStatus.Estimated);
        var alignment = new WordAlignment(tokens, tokens.Count - estimatedTokens, 0, estimatedTokens, 0);
        var cues = WordTimingAligner.BuildLineCues(lines.Select(l => l.Text).ToArray(), alignment, durationMs);
        if (cues is null) { issue = new("line_cues_unmatched"); return null; }
        // These cues are already short transcript segments and match the timed words exactly.
        return new AiAudioTimingResult(
            WordTimingAligner.ToWordTiming(alignment), cues, cues,
            "geminiTranscribeDirect", alignment, lines.ToArray());
    }

    private static bool ShouldSplit(IReadOnlyList<TranscribedWord> current, TranscribedWord next)
    {
        // Keep standalone punctuation with a spoken word, even if its timestamp or
        // speaker tag differs from the preceding word.
        if (WordTimingAligner.Tokenize([next.Text]).Count == 0
            || !current.Any(w => WordTimingAligner.Tokenize([w.Text]).Count > 0))
            return false;
        var last = current[^1];
        if (next.StartMs - last.EndMs >= PauseBoundaryMs)
            return true;
        if (!string.IsNullOrWhiteSpace(last.Speaker) && !string.IsNullOrWhiteSpace(next.Speaker)
            && last.Speaker != next.Speaker)
            return true;
        var text = last.Text.TrimEnd();
        var cjk = current.Any(w => ContainsCjkScript(w.Text)) || ContainsCjkScript(next.Text);
        if (cjk)
            return EndsAtCuePunctuation(text);
        return current.Count >= MaxWordsPerCue
            || current.Count >= 5 && text.Length > 0 && ".?!。？！".Contains(text[^1]);
    }

    private static bool ContainsCjkScript(string text) => text.EnumerateRunes().Any(r =>
        WordTimingAligner.IsCjk(r) || r.Value is >= 0x1100 and <= 0x11FF
            or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F
            or >= 0xAC00 and <= 0xD7AF or >= 0xD7B0 and <= 0xD7FF);

    private static bool EndsAtCuePunctuation(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (ClosingQuotes.Contains(text[i])) continue;
            return CuePunctuation.Contains(text[i]);
        }
        return false;
    }

    private static IReadOnlyList<TranscribedWord> SplitAtCjkPunctuation(TranscribedWord word)
    {
        var text = word.Text;
        if (!ContainsCjkScript(text)) return [word];

        var segments = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!CuePunctuation.Contains(text[i])
                || i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))
                continue;
            var end = i + 1;
            while (end < text.Length && (ClosingQuotes.Contains(text[end]) || CuePunctuation.Contains(text[end]))) end++;
            if (end < text.Length && WordTimingAligner.Tokenize([text[start..end]]).Count > 0
                && WordTimingAligner.Tokenize([text[end..]]).Count > 0)
            {
                segments.Add(text[start..end]);
                start = end;
            }
            i = end - 1;
        }
        if (segments.Count == 0) return [word];
        segments.Add(text[start..]);

        var duration = word.EndMs - word.StartMs;
        if (duration < segments.Count) return [word];
        var weights = segments.Select(segment => Math.Max(1,
            WordTimingAligner.Tokenize([segment]).Sum(token => Math.Max(1, token.Key.Length)))).ToArray();
        var total = weights.Sum();
        var used = 0;
        var cursorMs = word.StartMs;
        var fragments = new List<TranscribedWord>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            used += weights[i];
            var targetEnd = word.StartMs + (int)Math.Round((double)duration * used / total);
            var fragmentEnd = i == segments.Count - 1 ? word.EndMs
                : Math.Clamp(targetEnd, cursorMs + 1, word.EndMs - (segments.Count - i - 1));
            fragments.Add(word with { Text = segments[i], StartMs = cursorMs, EndMs = fragmentEnd });
            cursorMs = fragmentEnd;
        }
        return fragments;
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

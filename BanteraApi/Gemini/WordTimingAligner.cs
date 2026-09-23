using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BanteraApi.Videos;

namespace BanteraApi.Gemini;

/// <summary>A word heard by the transcription model, with its time in the audio.</summary>
public sealed record TranscribedWord(string Text, int StartMs, int EndMs, string? Speaker = null);

/// <summary>One word of the original script. <see cref="Key"/> is what spellings are compared on.</summary>
public sealed record ScriptToken(int Line, string Text, string Key);

/// <summary>The inclusive range of transcript word indices spoken for one script token.</summary>
public readonly record struct TokenMatch(int Start, int End);

public enum TokenTimingStatus
{
    /// <summary>Heard exactly as written.</summary>
    Exact,
    /// <summary>Heard differently (e.g. "7:00" for "seven") but matched to the script.</summary>
    Corrected,
    /// <summary>Not found in the transcript; timed between its neighbours.</summary>
    Estimated,
}

public sealed record TimedToken(int Line, string Text, int StartMs, int EndMs, TokenTimingStatus Status);

public sealed record WordAlignment(IReadOnlyList<TimedToken> Tokens, int Exact, int Corrected, int Estimated, int UnusedTranscriptWords)
{
    public double EstimatedRatio => Tokens.Count == 0 ? 1 : (double)Estimated / Tokens.Count;
}

/// <summary>
/// Maps word timestamps from a transcript back onto the ORIGINAL script: people read the
/// script, the transcript only supplies timing. Pure and deterministic; the AI step that
/// fixes words heard differently lives in <see cref="GeminiService.AlignTokensWithAiAsync"/>.
/// </summary>
public static class WordTimingAligner
{
    // Same pattern as the app player's word regex (_kWordTokenRe), so every timed word lines
    // up with a word the released app highlights and seeks to.
    private static readonly Regex AppWordRegex = new(@"[\p{L}\p{N}]+(?:['’ʼ][\p{L}\p{N}]+)*", RegexOptions.Compiled);

    private const long MaxDpCells = 30_000_000;
    private const int MinEstimatedMs = 80;
    private const int CueEndPaddingMs = 150;

    public static List<ScriptToken> Tokenize(IReadOnlyList<string> lines)
    {
        var tokens = new List<ScriptToken>();
        for (var line = 0; line < lines.Count; line++)
        {
            foreach (Match m in AppWordRegex.Matches(lines[line] ?? ""))
                tokens.Add(new ScriptToken(line, m.Value, NormalizeKey(m.Value)));
        }
        return tokens;
    }

    /// <summary>Lowercased letters and digits only. Marks are dropped so "नमस्ते" compares equal however it is split.</summary>
    public static string NormalizeKey(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (char.IsLetterOrDigit(ch) || category == UnicodeCategory.OtherLetter || category == UnicodeCategory.LetterNumber || category == UnicodeCategory.OtherNumber)
                sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Character-level edit-distance alignment. Handles differing word boundaries (Chinese
    /// comes back one character per word) but cannot know that "25" and "twenty five" match.
    /// </summary>
    public static TokenMatch?[] AlignByCharacters(IReadOnlyList<ScriptToken> tokens, IReadOnlyList<TranscribedWord> words)
    {
        var a = new List<char>();
        var aOwner = new List<int>();
        for (var t = 0; t < tokens.Count; t++)
            foreach (var ch in tokens[t].Key) { a.Add(ch); aOwner.Add(t); }

        var b = new List<char>();
        var bOwner = new List<int>();
        for (var w = 0; w < words.Count; w++)
            foreach (var ch in NormalizeKey(words[w].Text)) { b.Add(ch); bOwner.Add(w); }

        var result = new TokenMatch?[tokens.Count];
        int n = a.Count, m = b.Count;
        if (n == 0 || m == 0 || (long)(n + 1) * (m + 1) > MaxDpCells)
            return result;

        // 0 = diagonal (match / substitute), 1 = up (script char not heard), 2 = left (extra heard char)
        var trace = new byte[(n + 1) * (m + 1)];
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (var j = 0; j <= m; j++) { prev[j] = j; trace[j] = 2; }

        for (var i = 1; i <= n; i++)
        {
            cur[0] = i;
            trace[i * (m + 1)] = 1;
            for (var j = 1; j <= m; j++)
            {
                var best = prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                byte dir = 0;
                if (prev[j] + 1 < best) { best = prev[j] + 1; dir = 1; }
                if (cur[j - 1] + 1 < best) { best = cur[j - 1] + 1; dir = 2; }
                cur[j] = best;
                trace[i * (m + 1) + j] = dir;
            }
            (prev, cur) = (cur, prev);
        }

        // Walk back; prefer exact character matches per token, else substitutions.
        var exact = new (int Min, int Max)?[tokens.Count];
        var fuzzy = new (int Min, int Max)?[tokens.Count];
        static void Widen((int Min, int Max)?[] ranges, int t, int w) =>
            ranges[t] = ranges[t] is { } r ? (Math.Min(r.Min, w), Math.Max(r.Max, w)) : (w, w);

        int ii = n, jj = m;
        while (ii > 0 && jj > 0)
        {
            var dir = trace[ii * (m + 1) + jj];
            if (dir == 0)
            {
                Widen(a[ii - 1] == b[jj - 1] ? exact : fuzzy, aOwner[ii - 1], bOwner[jj - 1]);
                ii--; jj--;
            }
            else if (dir == 1) ii--;
            else jj--;
        }

        for (var t = 0; t < tokens.Count; t++)
            if ((exact[t] ?? fuzzy[t]) is { } r) result[t] = new TokenMatch(r.Min, r.Max);
        return result;
    }

    /// <summary>True when the words a token matched spell exactly that token.</summary>
    public static bool IsExact(ScriptToken token, TokenMatch? match, IReadOnlyList<TranscribedWord> words) =>
        match is { } m && token.Key.Length > 0 && HeardKey(words, m.Start, m.End) == token.Key;

    /// <summary>
    /// Keeps the character aligner's match wherever a token was heard exactly as written, and
    /// uses the AI's match only for tokens heard differently. An AI match that contradicts the
    /// exact anchors is dropped later by <see cref="Resolve"/>'s ordering check.
    /// </summary>
    public static TokenMatch?[] Merge(IReadOnlyList<ScriptToken> tokens, IReadOnlyList<TranscribedWord> words, IReadOnlyList<TokenMatch?> ai, IReadOnlyList<TokenMatch?> algorithm)
    {
        var merged = new TokenMatch?[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
            merged[i] = IsExact(tokens[i], algorithm[i], words) ? algorithm[i] : (i < ai.Count ? ai[i] : null) ?? algorithm[i];
        return merged;
    }

    /// <summary>
    /// Turns matches into a start/end for every script token. Out-of-range or backwards
    /// matches are dropped; tokens sharing transcript words split that time by length;
    /// unmatched tokens are spread between their matched neighbours.
    /// </summary>
    public static WordAlignment Resolve(IReadOnlyList<ScriptToken> tokens, IReadOnlyList<TranscribedWord> words, IReadOnlyList<TokenMatch?> rawMatches, int audioDurationMs)
    {
        var count = tokens.Count;
        var matches = new TokenMatch?[count];
        int lastStart = -1, lastEnd = -1;
        for (var i = 0; i < count; i++)
        {
            if (i < rawMatches.Count && rawMatches[i] is { } m
                && m.Start >= 0 && m.End >= m.Start && m.End < words.Count
                && m.Start >= lastStart && m.End >= lastEnd)
            {
                matches[i] = m;
                lastStart = m.Start;
                lastEnd = m.End;
            }
        }

        var start = new double[count];
        var end = new double[count];
        var status = new TokenTimingStatus[count];
        var timed = new bool[count];

        // Matched tokens, grouped where consecutive tokens share transcript words.
        for (var i = 0; i < count;)
        {
            if (matches[i] is not { } first) { i++; continue; }
            var j = i + 1;
            var maxEnd = first.End;
            while (j < count && matches[j] is { } next && next.Start <= maxEnd) { maxEnd = Math.Max(maxEnd, next.End); j++; }

            var spans = Spread(tokens, i, j, words[first.Start].StartMs, words[maxEnd].EndMs);
            var heard = HeardKey(words, first.Start, maxEnd);
            var written = string.Concat(Enumerable.Range(i, j - i).Select(k => tokens[k].Key));
            for (var k = i; k < j; k++)
            {
                (start[k], end[k]) = spans[k - i];
                status[k] = heard == written ? TokenTimingStatus.Exact : TokenTimingStatus.Corrected;
                timed[k] = true;
            }
            i = j;
        }

        // Unmatched runs: interpolate between neighbours.
        var lastWordEnd = words.Count > 0 ? words[^1].EndMs : 0;
        var duration = Math.Max(audioDurationMs, lastWordEnd);
        for (var i = 0; i < count;)
        {
            if (timed[i]) { i++; continue; }
            var j = i;
            while (j < count && !timed[j]) j++;
            var needed = MinEstimatedMs * (j - i);

            double from = i > 0 ? end[i - 1] : Math.Max(0, (words.Count > 0 ? words[0].StartMs : 0) - needed);
            double to = j < count ? start[j] : Math.Min(duration > 0 ? duration : from + needed, from + needed * 4.0);
            if (to - from < needed)
            {
                // No gap left: borrow the second half of the previous token's time.
                if (i > 0) { from = (start[i - 1] + end[i - 1]) / 2; end[i - 1] = from; }
                to = Math.Max(to, from + needed);
            }
            var spans = Spread(tokens, i, j, from, to);
            for (var k = i; k < j; k++) { (start[k], end[k]) = spans[k - i]; status[k] = TokenTimingStatus.Estimated; }
            i = j;
        }

        var used = new bool[words.Count];
        foreach (var m in matches)
            if (m is { } r) for (var w = r.Start; w <= r.End; w++) used[w] = true;

        var result = new List<TimedToken>(count);
        int exactCount = 0, correctedCount = 0, estimatedCount = 0;
        for (var i = 0; i < count; i++)
        {
            var s = (int)Math.Round(start[i]);
            var e = Math.Max(s + 1, (int)Math.Round(end[i]));
            result.Add(new TimedToken(tokens[i].Line, tokens[i].Text, s, e, status[i]));
            switch (status[i])
            {
                case TokenTimingStatus.Exact: exactCount++; break;
                case TokenTimingStatus.Corrected: correctedCount++; break;
                default: estimatedCount++; break;
            }
        }
        return new WordAlignment(result, exactCount, correctedCount, estimatedCount, used.Count(u => !u));
    }

    public static List<WordTimingRecord> ToWordTiming(WordAlignment alignment) =>
        alignment.Tokens
            .Select(t => new WordTimingRecord(t.Text, t.StartMs, t.EndMs, t.Status switch
            {
                TokenTimingStatus.Exact => 1.0,
                TokenTimingStatus.Corrected => 0.8,
                _ => 0.0,
            }))
            .ToList();

    /// <summary>
    /// One cue per dialogue line: first word start to last word end, with a little padding
    /// after the last word (never into the next line). Null if a line has no words.
    /// </summary>
    public static List<VideoTranscriptCueRecord>? BuildLineCues(IReadOnlyList<string> lines, WordAlignment alignment, int durationMs)
    {
        var spans = new List<(int Start, int End)>(lines.Count);
        for (var line = 0; line < lines.Count; line++)
        {
            var lineTokens = alignment.Tokens.Where(t => t.Line == line).ToList();
            if (lineTokens.Count == 0) return null;
            spans.Add((lineTokens[0].StartMs, lineTokens[^1].EndMs));
        }
        return PadAndBuild(spans, lines, durationMs);
    }

    /// <summary>
    /// Short practice cues: each line split at its validated short-cue texts, timed from the
    /// words each piece contains. Null when a piece's words do not line up with the line's.
    /// </summary>
    public static List<VideoTranscriptCueRecord>? BuildShortCues(IReadOnlyList<DialogueLine> lines, WordAlignment alignment, int durationMs)
    {
        var spans = new List<(int Start, int End)>();
        var texts = new List<string>();
        for (var line = 0; line < lines.Count; line++)
        {
            var lineTokens = alignment.Tokens.Where(t => t.Line == line).ToList();
            var pieces = lines[line].ShortCues.Count > 0 ? lines[line].ShortCues : [lines[line].Text];
            var cursor = 0;
            foreach (var piece in pieces)
            {
                var pieceWords = AppWordRegex.Matches(piece).Select(m => m.Value).ToList();
                if (pieceWords.Count == 0 || cursor + pieceWords.Count > lineTokens.Count) return null;
                for (var k = 0; k < pieceWords.Count; k++)
                    if (!string.Equals(pieceWords[k], lineTokens[cursor + k].Text, StringComparison.Ordinal)) return null;

                spans.Add((lineTokens[cursor].StartMs, lineTokens[cursor + pieceWords.Count - 1].EndMs));
                texts.Add(piece);
                cursor += pieceWords.Count;
            }
            if (cursor != lineTokens.Count) return null;
        }
        return PadAndBuild(spans, texts, durationMs);
    }

    private static List<VideoTranscriptCueRecord> PadAndBuild(List<(int Start, int End)> spans, IReadOnlyList<string> texts, int durationMs)
    {
        var cues = new List<VideoTranscriptCueRecord>(spans.Count);
        var previousEnd = 0;
        for (var i = 0; i < spans.Count; i++)
        {
            var start = Math.Max(spans[i].Start, previousEnd);
            var limit = i + 1 < spans.Count ? spans[i + 1].Start : Math.Max(durationMs, spans[i].End);
            var end = Math.Max(spans[i].End, Math.Min(spans[i].End + CueEndPaddingMs, limit));
            if (end <= start) end = start + 1;
            cues.Add(new VideoTranscriptCueRecord(i, start, end, texts[i]));
            previousEnd = end;
        }
        return cues;
    }

    private static string HeardKey(IReadOnlyList<TranscribedWord> words, int from, int to)
    {
        var sb = new StringBuilder();
        for (var w = from; w <= to; w++) sb.Append(NormalizeKey(words[w].Text));
        return sb.ToString();
    }

    /// <summary>Splits [from, to] across tokens[i..j) in proportion to their length.</summary>
    private static List<(double Start, double End)> Spread(IReadOnlyList<ScriptToken> tokens, int i, int j, double from, double to)
    {
        double Weight(ScriptToken t) => Math.Max(1, t.Key.Length > 0 ? t.Key.Length : t.Text.Length);
        var total = 0.0;
        for (var k = i; k < j; k++) total += Weight(tokens[k]);
        var spans = new List<(double, double)>(j - i);
        var at = from;
        for (var k = i; k < j; k++)
        {
            var next = at + (to - from) * Weight(tokens[k]) / total;
            spans.Add((at, next));
            at = next;
        }
        return spans;
    }
}

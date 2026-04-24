using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BanteraApi.Gemini;

namespace BanteraApi.Videos;

public static class RevAiCueAlignmentBuilder
{
    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}']+", RegexOptions.Compiled);
    private const int StrictMatchLookaheadWindow = 6;
    private const int StrictFirstTokenLookaheadWindow = 20;
    private const double StrictMinLineMatchRatio = 0.7;
    private const int StrictMinMatchedWordsPerLine = 2;
    private const int TolerantMatchLookaheadWindow = 12;
    private const int TolerantFirstTokenLookaheadWindow = 30;
    private const int BoundaryFirstTokenLookaheadWindow = 30;
    private const int BoundaryEndLookaheadExtra = 20;

    public sealed record AlignmentFailure(
        int LineIndex,
        int MatchedWords,
        int ExpectedWords,
        double MatchRatio,
        double RequiredMatchRatio,
        int RequiredMatchedWords,
        string? ExpectedToken,
        string? ActualWord);

    public static bool TryBuild(
        DialogueLine[] lines,
        IReadOnlyList<WordTimingRecord> wordTiming,
        out IReadOnlyList<VideoTranscriptCueRecord>? cues,
        out AlignmentFailure? failure)
    {
        cues = null;
        failure = null;
        if (lines.Length == 0 || wordTiming.Count == 0)
        {
            failure = new AlignmentFailure(0, 0, 0, 0, StrictMinLineMatchRatio, StrictMinMatchedWordsPerLine, null, null);
            return false;
        }
        return TryBuildCore(
            lines,
            wordTiming,
            BuildMode.Strict,
            out cues,
            out failure);
    }

    public static bool TryBuildTolerant(
        DialogueLine[] lines,
        IReadOnlyList<WordTimingRecord> wordTiming,
        out IReadOnlyList<VideoTranscriptCueRecord>? cues,
        out AlignmentFailure? failure)
    {
        cues = null;
        failure = null;
        if (lines.Length == 0 || wordTiming.Count == 0)
        {
            failure = new AlignmentFailure(0, 0, 0, 0, 0.7, 2, null, null);
            return false;
        }
        return TryBuildCore(
            lines,
            wordTiming,
            BuildMode.Tolerant,
            out cues,
            out failure);
    }

    public static bool TryBuildBoundary(
        DialogueLine[] lines,
        IReadOnlyList<WordTimingRecord> wordTiming,
        out IReadOnlyList<VideoTranscriptCueRecord>? cues,
        out AlignmentFailure? failure)
    {
        cues = null;
        failure = null;
        if (lines.Length == 0 || wordTiming.Count == 0)
        {
            failure = new AlignmentFailure(0, 0, 0, 0, 1, 2, null, null);
            return false;
        }

        var result = new List<VideoTranscriptCueRecord>(lines.Length);
        var timingCursor = 0;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var tokens = Tokenize(lines[lineIndex].Text);
            if (tokens.Count == 0)
            {
                failure = new AlignmentFailure(lineIndex, 0, 0, 0, 1, 2, null, null);
                return false;
            }

            var firstIndex = FindTokenIndex(
                wordTiming,
                tokens[0],
                timingCursor,
                Math.Min(wordTiming.Count, timingCursor + BoundaryFirstTokenLookaheadWindow));
            if (firstIndex < 0)
            {
                failure = new AlignmentFailure(
                    lineIndex,
                    0,
                    tokens.Count,
                    0,
                    1,
                    Math.Min(2, tokens.Count),
                    tokens[0],
                    timingCursor < wordTiming.Count ? wordTiming[timingCursor].Word : null);
                return false;
            }

            var lastIndex = firstIndex;
            if (tokens.Count > 1)
            {
                var endSearchLimit = Math.Min(wordTiming.Count, firstIndex + tokens.Count + BoundaryEndLookaheadExtra);
                lastIndex = FindTokenIndex(wordTiming, tokens[^1], firstIndex, endSearchLimit);
                if (lastIndex < 0)
                {
                    failure = new AlignmentFailure(
                        lineIndex,
                        1,
                        tokens.Count,
                        1d / tokens.Count,
                        1,
                        2,
                        tokens[^1],
                        firstIndex + 1 < wordTiming.Count ? wordTiming[firstIndex + 1].Word : null);
                    return false;
                }
            }

            var lineStart = Math.Max(0, wordTiming[firstIndex].StartMs);
            var lineEnd = Math.Max(lineStart + 1, wordTiming[lastIndex].EndMs);
            result.Add(new VideoTranscriptCueRecord(lineIndex, lineStart, lineEnd, lines[lineIndex].Text));
            timingCursor = Math.Max(timingCursor, lastIndex + 1);
        }

        cues = result;
        return true;
    }

    public static bool TryBuildShortCueBoundary(
        DialogueLine[] lines,
        IReadOnlyList<VideoTranscriptCueRecord> longCues,
        IReadOnlyList<WordTimingRecord> wordTiming,
        out IReadOnlyList<VideoTranscriptCueRecord>? shortCues,
        out AlignmentFailure? failure)
    {
        shortCues = null;
        failure = null;
        if (lines.Length == 0 || longCues.Count == 0 || wordTiming.Count == 0)
        {
            failure = new AlignmentFailure(0, 0, 0, 0, 1, 2, null, null);
            return false;
        }

        var segments = new List<CueSegment>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var parentCue = longCues.FirstOrDefault(cue => cue.Index == lineIndex);
            if (parentCue is null)
            {
                failure = new AlignmentFailure(lineIndex, 0, 0, 0, 1, 2, null, null);
                return false;
            }

            var cueTexts = lines[lineIndex].ShortCues.Count > 0
                ? lines[lineIndex].ShortCues
                : [lines[lineIndex].Text];
            var candidates = BuildShortCueCandidates(cueTexts, wordTiming, parentCue, lineIndex, out failure);
            if (candidates is null)
                return false;

            if (!AddRepairedShortCueSegments(candidates, segments, out failure))
                return false;
        }

        if (segments.Count == 0)
        {
            failure = new AlignmentFailure(0, 0, 0, 0, 1, 2, null, null);
            return false;
        }

        shortCues = segments
            .Select((segment, index) => new VideoTranscriptCueRecord(index, segment.StartMs, segment.EndMs, segment.Text))
            .ToArray();
        return true;
    }

    private static bool TryBuildCore(
        DialogueLine[] lines,
        IReadOnlyList<WordTimingRecord> wordTiming,
        BuildMode mode,
        out IReadOnlyList<VideoTranscriptCueRecord>? cues,
        out AlignmentFailure? failure)
    {
        cues = null;
        failure = null;

        var result = new List<VideoTranscriptCueRecord>(lines.Length);
        var timingCursor = 0;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var tokens = Tokenize(lines[lineIndex].Text);
            if (tokens.Count == 0)
            {
                var (requiredMatchRatio, requiredMatchedWords) = GetThresholds(mode, 0);
                failure = new AlignmentFailure(lineIndex, 0, 0, 0, requiredMatchRatio, requiredMatchedWords, null, null);
                return false;
            }

            var lineStart = -1;
            var lineEnd = -1;
            var matchedCount = 0;
            var failedToken = string.Empty;
            var actualWord = string.Empty;

            for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
            {
                var token = tokens[tokenIndex];
                var lookahead = tokenIndex == 0
                    ? (mode == BuildMode.Strict ? StrictFirstTokenLookaheadWindow : TolerantFirstTokenLookaheadWindow)
                    : (mode == BuildMode.Strict ? StrictMatchLookaheadWindow : TolerantMatchLookaheadWindow);
                var maxSearch = Math.Min(wordTiming.Count, timingCursor + lookahead);
                var bestMatch = -1;
                for (var cursor = timingCursor; cursor < maxSearch; cursor++)
                {
                    if (NormalizeToken(wordTiming[cursor].Word) == token)
                    {
                        bestMatch = cursor;
                        break;
                    }
                }

                if (bestMatch < 0)
                {
                    failedToken = token;
                    actualWord = timingCursor < wordTiming.Count ? wordTiming[timingCursor].Word : string.Empty;
                    continue;
                }

                var matchedWord = wordTiming[bestMatch];
                var startMs = Math.Max(0, matchedWord.StartMs);
                var endMs = Math.Max(startMs + 1, matchedWord.EndMs);
                lineStart = lineStart < 0 ? startMs : Math.Min(lineStart, startMs);
                lineEnd = endMs;
                timingCursor = bestMatch + 1;
                matchedCount++;
            }

            var matchRatio = tokens.Count == 0
                ? 0
                : (double)matchedCount / tokens.Count;
            var (requiredMatchRatioForLine, requiredMatchedWordsForLine) = GetThresholds(mode, tokens.Count);
            var meetsThreshold = matchedCount >= Math.Min(requiredMatchedWordsForLine, tokens.Count)
                && matchRatio >= requiredMatchRatioForLine;

            if (!meetsThreshold || lineStart < 0 || lineEnd <= lineStart)
            {
                failure = new AlignmentFailure(
                    lineIndex,
                    matchedCount,
                    tokens.Count,
                    matchRatio,
                    requiredMatchRatioForLine,
                    Math.Min(requiredMatchedWordsForLine, tokens.Count),
                    string.IsNullOrWhiteSpace(failedToken) ? null : failedToken,
                    string.IsNullOrWhiteSpace(actualWord) ? null : actualWord);
                return false;
            }

            result.Add(new VideoTranscriptCueRecord(
                lineIndex,
                lineStart,
                lineEnd,
                lines[lineIndex].Text));
        }

        cues = result;
        return true;
    }

    private static (double RequiredMatchRatio, int RequiredMatchedWords) GetThresholds(BuildMode mode, int expectedWords)
    {
        if (mode == BuildMode.Strict)
            return (StrictMinLineMatchRatio, StrictMinMatchedWordsPerLine);

        var requiredMatchedWords = expectedWords >= 3 ? 3 : expectedWords;
        var requiredMatchRatio = expectedWords >= 8 ? 0.55 : 0.7;
        return (requiredMatchRatio, requiredMatchedWords);
    }

    private enum BuildMode
    {
        Strict,
        Tolerant,
    }

    private static List<ShortCueCandidate>? BuildShortCueCandidates(
        IReadOnlyList<string> cueTexts,
        IReadOnlyList<WordTimingRecord> wordTiming,
        VideoTranscriptCueRecord parentCue,
        int lineIndex,
        out AlignmentFailure? failure)
    {
        failure = null;
        var candidates = new List<ShortCueCandidate>(cueTexts.Count);
        var parentStartIndex = FindFirstWordIndexInRange(wordTiming, parentCue.StartMs, parentCue.EndMs);
        var parentEndExclusive = FindEndExclusiveWordIndexInRange(wordTiming, parentCue.StartMs, parentCue.EndMs);
        if (parentStartIndex < 0 || parentEndExclusive <= parentStartIndex)
        {
            failure = new AlignmentFailure(lineIndex, 0, 0, 0, 1, 2, null, null);
            return null;
        }

        var cursor = parentStartIndex;
        foreach (var cueText in cueTexts)
        {
            var tokens = Tokenize(cueText);
            if (tokens.Count == 0)
            {
                failure = new AlignmentFailure(lineIndex, 0, 0, 0, 1, 2, null, null);
                return null;
            }

            var firstIndex = FindTokenIndex(wordTiming, tokens[0], cursor, parentEndExclusive);
            var endSearchStart = firstIndex >= 0 ? firstIndex : cursor;
            var lastIndex = tokens.Count == 1 && firstIndex >= 0
                ? firstIndex
                : FindTokenIndex(wordTiming, tokens[^1], endSearchStart, parentEndExclusive);

            candidates.Add(new ShortCueCandidate(
                cueText,
                firstIndex >= 0 ? Math.Max(0, wordTiming[firstIndex].StartMs) : null,
                lastIndex >= 0 ? Math.Max(0, wordTiming[lastIndex].EndMs) : null,
                parentCue.StartMs,
                parentCue.EndMs));

            if (lastIndex >= cursor)
                cursor = lastIndex + 1;
            else if (firstIndex >= cursor)
                cursor = firstIndex + 1;
        }

        if (candidates.Count > 0)
        {
            if (!candidates[0].StartMs.HasValue)
                candidates[0] = candidates[0] with { StartMs = parentCue.StartMs };
            var last = candidates.Count - 1;
            if (!candidates[last].EndMs.HasValue)
                candidates[last] = candidates[last] with { EndMs = parentCue.EndMs };
        }

        return candidates;
    }

    private static bool AddRepairedShortCueSegments(
        List<ShortCueCandidate> candidates,
        List<CueSegment> output,
        out AlignmentFailure? failure)
    {
        failure = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var hasStart = candidate.StartMs.HasValue;
            var hasEnd = candidate.EndMs.HasValue;

            if (!hasStart && !hasEnd)
            {
                if (output.Count > 0)
                    MergeIntoPrevious(output, candidate.Text, null);
                else if (i + 1 < candidates.Count)
                    candidates[i + 1] = PrependToNext(candidates[i + 1], candidate.Text, candidate.ParentStartMs);
                else
                    return FailShortCue(i, candidate, out failure);
                continue;
            }

            if (!hasStart)
            {
                if (output.Count > 0)
                    MergeIntoPrevious(output, candidate.Text, candidate.EndMs);
                else
                    AddSegment(output, candidate.Text, candidate.ParentStartMs, candidate.EndMs!.Value);
                continue;
            }

            if (!hasEnd)
            {
                if (i + 1 < candidates.Count)
                    candidates[i + 1] = PrependToNext(candidates[i + 1], candidate.Text, candidate.StartMs);
                else
                    AddSegment(output, candidate.Text, candidate.StartMs!.Value, candidate.ParentEndMs);
                continue;
            }

            AddSegment(output, candidate.Text, candidate.StartMs!.Value, candidate.EndMs!.Value);
        }

        return output.All(segment => segment.EndMs > segment.StartMs);
    }

    private static bool FailShortCue(int index, ShortCueCandidate candidate, out AlignmentFailure failure)
    {
        var tokens = Tokenize(candidate.Text);
        failure = new AlignmentFailure(
            index,
            0,
            tokens.Count,
            0,
            1,
            Math.Min(2, tokens.Count),
            tokens.FirstOrDefault(),
            null);
        return false;
    }

    private static void AddSegment(List<CueSegment> output, string text, int startMs, int endMs)
    {
        var start = Math.Max(0, startMs);
        var end = Math.Max(start + 1, endMs);
        if (output.Count > 0 && start < output[^1].EndMs)
            start = output[^1].EndMs;
        if (end <= start)
            end = start + 1;
        output.Add(new CueSegment(text, start, end));
    }

    private static void MergeIntoPrevious(List<CueSegment> output, string text, int? endMs)
    {
        var previous = output[^1];
        var mergedEnd = endMs.HasValue
            ? Math.Max(previous.EndMs, endMs.Value)
            : previous.EndMs;
        output[^1] = previous with
        {
            Text = MergeText(previous.Text, text),
            EndMs = Math.Max(previous.StartMs + 1, mergedEnd),
        };
    }

    private static ShortCueCandidate PrependToNext(ShortCueCandidate next, string text, int? startMs)
    {
        return next with
        {
            Text = MergeText(text, next.Text),
            StartMs = startMs ?? next.StartMs,
        };
    }

    private static int FindTokenIndex(
        IReadOnlyList<WordTimingRecord> wordTiming,
        string token,
        int startInclusive,
        int endExclusive)
    {
        var start = Math.Clamp(startInclusive, 0, wordTiming.Count);
        var end = Math.Clamp(endExclusive, start, wordTiming.Count);
        for (var i = start; i < end; i++)
        {
            if (NormalizeToken(wordTiming[i].Word) == token)
                return i;
        }

        return -1;
    }

    private static int FindFirstWordIndexInRange(IReadOnlyList<WordTimingRecord> wordTiming, int startMs, int endMs)
    {
        for (var i = 0; i < wordTiming.Count; i++)
        {
            if (wordTiming[i].EndMs > startMs && wordTiming[i].StartMs < endMs)
                return i;
        }

        return -1;
    }

    private static int FindEndExclusiveWordIndexInRange(IReadOnlyList<WordTimingRecord> wordTiming, int startMs, int endMs)
    {
        var endExclusive = -1;
        for (var i = 0; i < wordTiming.Count; i++)
        {
            if (wordTiming[i].EndMs > startMs && wordTiming[i].StartMs < endMs)
                endExclusive = i + 1;
        }

        return endExclusive;
    }

    private static string MergeText(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second.Trim();
        if (string.IsNullOrWhiteSpace(second))
            return first.Trim();
        return $"{first.Trim()} {second.Trim()}";
    }

    private sealed record ShortCueCandidate(
        string Text,
        int? StartMs,
        int? EndMs,
        int ParentStartMs,
        int ParentEndMs);

    private sealed record CueSegment(string Text, int StartMs, int EndMs);

    internal static List<string> Tokenize(string text)
    {
        var normalizedText = NormalizeApostrophes(text ?? string.Empty);
        return TokenRegex
            .Matches(normalizedText)
            .Select(match => NormalizeToken(match.Value))
            .Where(token => token.Length > 0)
            .ToList();
    }

    internal static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = NormalizeApostrophes(value.Trim());
        normalized = RemoveDiacritics(normalized);
        normalized = normalized.ToLowerInvariant();
        normalized = TokenRegex.Match(normalized).Value;
        return normalized;
    }

    private static string NormalizeApostrophes(string value)
    {
        return (value ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Replace('\u2018', '\'')
            .Replace('\u2019', '\'')
            .Replace('\u02BC', '\'')
            .Replace('\uFF07', '\'');
    }

    private static string RemoveDiacritics(string text)
    {
        var decomposition = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposition.Length);
        foreach (var c in decomposition)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}

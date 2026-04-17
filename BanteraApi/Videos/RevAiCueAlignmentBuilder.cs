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

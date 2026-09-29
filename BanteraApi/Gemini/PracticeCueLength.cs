namespace BanteraApi.Gemini;

/// <summary>Length preferences never justify splitting away from punctuation or merging speakers.</summary>
internal static class PracticeCueLength
{
    private const int MaxCueMs = 8000;

    public static bool IsShort(string text, int? durationMs = null)
    {
        var tokens = WordTimingAligner.Tokenize([text]);
        var cjk = text.EnumerateRunes().Any(WordTimingAligner.IsCjk);
        return tokens.Count < (cjk ? 4 : 3) || durationMs is < 700;
    }

    public static bool CanCombine(string first, string second, int? durationMs = null)
    {
        var text = first + " " + second;
        var cjk = text.EnumerateRunes().Any(WordTimingAligner.IsCjk);
        return WordTimingAligner.Tokenize([text]).Count <= (cjk ? 36 : 18)
            && (durationMs is null || durationMs <= MaxCueMs);
    }

    /// <summary>Only used for already validated pieces within one dialogue line/speaker.</summary>
    public static IReadOnlyList<string> CombineShortPieces(string line, IReadOnlyList<string> pieces)
    {
        if (pieces.Count < 2) return pieces;
        var ranges = new List<(int Start, int End)>();
        var cursor = 0;
        foreach (var piece in pieces)
        {
            var start = line.IndexOf(piece, cursor, StringComparison.Ordinal);
            if (start < 0) return pieces;
            cursor = start + piece.Length;
            ranges.Add((start, cursor));
        }

        for (var i = 0; i < ranges.Count; i++)
        {
            var current = line[ranges[i].Start..ranges[i].End];
            if (!IsShort(current)) continue;
            // Prefer the following phrase; use the preceding phrase for a short tail.
            if (i + 1 < ranges.Count && CanCombine(current, line[ranges[i + 1].Start..ranges[i + 1].End]))
            {
                ranges[i] = (ranges[i].Start, ranges[i + 1].End);
                ranges.RemoveAt(i + 1);
                i--;
            }
            else if (i > 0 && CanCombine(line[ranges[i - 1].Start..ranges[i - 1].End], current))
            {
                ranges[i - 1] = (ranges[i - 1].Start, ranges[i].End);
                ranges.RemoveAt(i);
                i--;
            }
        }
        return ranges.Select(range => line[range.Start..range.End]).ToArray();
    }
}

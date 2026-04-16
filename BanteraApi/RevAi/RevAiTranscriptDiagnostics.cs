using System.Security.Cryptography;
using System.Text;

namespace BanteraApi.RevAi;

public sealed record RevAiTranscriptDiagnostics(
    int CharCount,
    int LineCount,
    string TranscriptHash,
    string NormalizedTranscriptHash,
    string TranscriptPreview)
{
    public static RevAiTranscriptDiagnostics Create(string transcript, int previewMaxChars)
    {
        var safeTranscript = transcript ?? string.Empty;
        var normalized = NormalizeForHash(safeTranscript);
        var preview = BuildPreview(safeTranscript, previewMaxChars);
        return new RevAiTranscriptDiagnostics(
            safeTranscript.Length,
            CountLines(safeTranscript),
            Hash(safeTranscript),
            Hash(normalized),
            preview);
    }

    public static string BuildPreview(string transcript, int maxChars)
    {
        if (string.IsNullOrEmpty(transcript))
            return string.Empty;

        var safeMax = Math.Max(1, maxChars);
        if (transcript.Length <= safeMax)
            return transcript;

        return $"{transcript[..safeMax]}...";
    }

    public static string NormalizeForHash(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();
    }

    private static int CountLines(string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        return value.Count(c => c == '\n') + 1;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

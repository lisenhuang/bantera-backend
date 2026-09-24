using BanteraApi.Videos;
using ModelContextProtocol;

namespace BanteraApi.Mcp;

/// <summary>Checks an authored lesson before any uploaded object becomes visible to app users.</summary>
public static class PracticeAudioImportValidator
{
    public const int MaxAudioBytes = 30 * 1024 * 1024;
    public const int MaxCoverBytes = 5 * 1024 * 1024;
    public const int MaxDurationMs = 30 * 60 * 1000;

    public static string AudioExtension(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "mp3" => ".mp3",
        "wav" => ".wav",
        _ => throw new McpException("audioFormat must be 'mp3' or 'wav'."),
    };

    public static string AudioContentType(string format)
        => AudioExtension(format) == ".mp3" ? "audio/mpeg" : "audio/wav";

    public static string Validate(
        string? title,
        string? language,
        string? languageCode,
        string? transcriptText,
        int durationMs,
        string[]? dialogueLines,
        VideoTranscriptCue[]? cues,
        VideoTranscriptCue[]? shortCues,
        WordTimingRecord[]? words)
    {
        var cleanTitle = title?.Trim() ?? "";
        if (cleanTitle.Length is < 1 or > 180 || cleanTitle.IndexOfAny(['/', '\\', '\r', '\n']) >= 0)
            throw new McpException("title must be 1-180 characters without path separators or line breaks.");
        if (string.IsNullOrWhiteSpace(language) || language.Length > 80)
            throw new McpException("transcriptLanguage must be 1-80 characters.");
        if (string.IsNullOrWhiteSpace(languageCode) || languageCode.Length > 16
            || !System.Text.RegularExpressions.Regex.IsMatch(languageCode, "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$"))
            throw new McpException("transcriptLanguageCode must be a valid short BCP-47 code.");
        if (durationMs is < 1 or > MaxDurationMs)
            throw new McpException("durationMs must be between 1 ms and 30 minutes.");
        if (dialogueLines is not { Length: > 0 and <= 1000 }
            || cues is not { Length: > 0 and <= 1000 }
            || dialogueLines.Length != cues.Length)
            throw new McpException("dialogueLines and transcriptCues must contain the same 1-1000 lines.");
        if (shortCues is { Length: > 5000 })
            throw new McpException("transcriptShortCues may contain at most 5000 cues.");
        if (words is not { Length: > 0 and <= 15000 })
            throw new McpException("wordTiming must contain 1-15000 words.");

        ValidateCues(cues, durationMs, "transcriptCues");
        if (shortCues is { Length: > 0 })
        {
            ValidateCues(shortCues, durationMs, "transcriptShortCues");
            var fullText = string.Concat(cues.Select(c => c.Text).SelectMany(t => t.Where(ch => !char.IsWhiteSpace(ch))));
            var shortText = string.Concat(shortCues.Select(c => c.Text).SelectMany(t => t.Where(ch => !char.IsWhiteSpace(ch))));
            if (!string.Equals(fullText, shortText, StringComparison.Ordinal))
                throw new McpException("transcriptShortCues must contain the same text as transcriptCues.");
        }

        for (var i = 0; i < dialogueLines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(dialogueLines[i]) || dialogueLines[i].Length > 2000
                || dialogueLines[i].Trim() != cues[i].Text.Trim())
                throw new McpException($"dialogueLines[{i}] must match transcriptCues[{i}].text.");
        }

        var transcript = transcriptText?.Replace("\r\n", "\n").Trim() ?? "";
        if (transcript.Length is < 1 or > 100_000)
            throw new McpException("transcriptText must contain 1-100,000 characters.");
        var cueTranscript = string.Join("\n", cues.Select(c => c.Text.Trim()));
        if (!string.Equals(transcript, cueTranscript, StringComparison.Ordinal))
            throw new McpException("transcriptText must match the text of transcriptCues in order.");

        var previousStart = -1;
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (string.IsNullOrWhiteSpace(word.Word) || word.Word.Length > 100
                || word.StartMs < 0 || word.EndMs <= word.StartMs || word.EndMs > durationMs
                || word.StartMs < previousStart
                || word.Confidence is < 0 or > 1)
                throw new McpException($"wordTiming[{i}] has invalid text, timing, order, or confidence.");
            previousStart = word.StartMs;

            if (word.Parts is { Count: > 0 })
            {
                if (word.Parts.Count > 100)
                    throw new McpException($"wordTiming[{i}].parts has too many entries.");
                var previousPartStart = -1;
                foreach (var part in word.Parts)
                {
                    if (string.IsNullOrWhiteSpace(part.Word) || part.Word.Length > 20
                        || part.StartMs < word.StartMs || part.EndMs > word.EndMs
                        || part.EndMs <= part.StartMs || part.StartMs < previousPartStart)
                        throw new McpException($"wordTiming[{i}].parts contains invalid timing or text.");
                    previousPartStart = part.StartMs;
                }
            }
        }

        return transcript;
    }

    private static void ValidateCues(VideoTranscriptCue[] cues, int durationMs, string name)
    {
        var previousStart = -1;
        for (var i = 0; i < cues.Length; i++)
        {
            var cue = cues[i];
            if (cue.Index != i || cue.StartMs < 0 || cue.EndMs <= cue.StartMs
                || cue.EndMs > durationMs || cue.StartMs < previousStart
                || string.IsNullOrWhiteSpace(cue.Text) || cue.Text.Length > 2000)
                throw new McpException($"{name}[{i}] has invalid index, text, timing, or order.");
            previousStart = cue.StartMs;
        }
    }
}

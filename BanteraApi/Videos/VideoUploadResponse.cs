using System.Text.Json.Serialization;

namespace BanteraApi.Videos;

public sealed record WordTimingDto(
    string Word,
    int StartMs,
    int EndMs,
    double? Confidence)
{
    /// <summary>
    /// Per-character timing inside a Chinese / Japanese word (the word is one clause-long run
    /// for the app's word pattern). Omitted for other words; older apps ignore it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WordTimingPart>? Parts { get; init; }
}

public sealed record VideoUploadResponse(
    Guid Id,
    Guid UserId,
    string OriginalFileName,
    string TranscriptText,
    string TranscriptLanguage,
    string TranscriptLanguageCode,
    IReadOnlyList<VideoTranscriptCue> TranscriptCues,
    IReadOnlyList<VideoTranscriptCue>? TranscriptShortCues,
    bool IsPublic,
    int DurationMs,
    long FileSizeBytes,
    int? VideoWidth,
    int? VideoHeight,
    string VideoContentType,
    string? VideoUrl,
    string? CoverImageUrl,
    bool IsAiGenerated,
    bool IsTranscriptionEstimated,
    DateTime CreatedAt,
    string? CreatorDisplayName,
    int? TranscriptionVersion,
    IReadOnlyList<string>? DialogueLines,
    IReadOnlyList<WordTimingDto>? WordTiming);

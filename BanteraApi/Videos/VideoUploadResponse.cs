namespace BanteraApi.Videos;

public sealed record VideoUploadResponse(
    Guid Id,
    Guid UserId,
    string OriginalFileName,
    string TranscriptText,
    string TranscriptLanguage,
    string TranscriptLanguageCode,
    IReadOnlyList<VideoTranscriptCue> TranscriptCues,
    bool IsPublic,
    int DurationMs,
    long FileSizeBytes,
    int? VideoWidth,
    int? VideoHeight,
    string VideoContentType,
    string? VideoUrl,
    bool IsAiGenerated,
    bool IsTranscriptionEstimated,
    DateTime CreatedAt);

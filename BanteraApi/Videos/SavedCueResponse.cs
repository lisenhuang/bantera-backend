namespace BanteraApi.Videos;

public sealed record SavedCueResponse(
    Guid Id,
    string CueId,
    int CueIndex,
    string? CueText,
    int? StartTimeMs,
    int? EndTimeMs,
    string? CueMode,
    string? ParentCueId,
    int? ParentCueIndex,
    DateTime SavedAt,
    VideoUploadResponse Video);

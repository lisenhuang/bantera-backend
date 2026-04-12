namespace BanteraApi.Videos;

public sealed record SavedCueResponse(
    Guid Id,
    string CueId,
    int CueIndex,
    DateTime SavedAt,
    VideoUploadResponse Video);

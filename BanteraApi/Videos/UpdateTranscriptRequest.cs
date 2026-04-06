namespace BanteraApi.Videos;

public record UpdateTranscriptRequest(
    string TranscriptText,
    IReadOnlyList<VideoTranscriptCue> TranscriptCues);

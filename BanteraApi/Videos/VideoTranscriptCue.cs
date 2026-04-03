namespace BanteraApi.Videos;

public sealed record VideoTranscriptCue(
    int Index,
    int StartMs,
    int EndMs,
    string Text);

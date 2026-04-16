namespace BanteraApi.Videos;

public sealed record WordTimingRecord(
    string Word,
    int StartMs,
    int EndMs,
    double? Confidence);

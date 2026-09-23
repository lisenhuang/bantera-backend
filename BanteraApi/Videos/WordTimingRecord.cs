using System.Text.Json.Serialization;

namespace BanteraApi.Videos;

public sealed record WordTimingRecord(
    string Word,
    int StartMs,
    int EndMs,
    double? Confidence)
{
    /// <summary>Per-character timing for Chinese / Japanese words; null for other words.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WordTimingPart>? Parts { get; init; }
}

public sealed record WordTimingPart(string Word, int StartMs, int EndMs);

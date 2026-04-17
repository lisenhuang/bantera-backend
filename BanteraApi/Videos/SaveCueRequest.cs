namespace BanteraApi.Videos;

public sealed record SaveCueRequest
{
    public Guid VideoId { get; init; }
    public string CueId { get; init; } = string.Empty;
    public int CueIndex { get; init; }
    public string? CueText { get; init; }
    public int? StartTimeMs { get; init; }
    public int? EndTimeMs { get; init; }
    public string? CueMode { get; init; }
    public string? ParentCueId { get; init; }
    public int? ParentCueIndex { get; init; }
}

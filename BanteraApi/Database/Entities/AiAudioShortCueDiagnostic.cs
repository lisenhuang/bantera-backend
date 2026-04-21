namespace BanteraApi.Database.Entities;

public class AiAudioShortCueDiagnostic
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VideoId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string LanguageCode { get; set; } = "";
    public string? ScenarioId { get; set; }
    public string Reason { get; set; } = "";
    public string? LongAlignmentMode { get; set; }
    public bool ShortAlignmentAttempted { get; set; }
    public int LineCount { get; set; }
    public int LinesWithSplitCount { get; set; }
    public int FlattenedShortCueCount { get; set; }
    public int LongCueCount { get; set; }
    public int WordTimingCount { get; set; }
    public string? DetailJson { get; set; }
}

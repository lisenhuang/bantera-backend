namespace BanteraApi.Database.Entities;

public sealed class AdminAudioTest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CreatedByUserId { get; set; }
    public Guid? SourceTestId { get; set; }
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "queued";
    public string LanguageCode { get; set; } = "";
    public string Language { get; set; } = "";
    public int TargetDurationSeconds { get; set; }
    public string TextModel { get; set; } = "";
    public string AudioModel { get; set; } = "";
    public string? Title { get; set; }
    public string? DialogueJson { get; set; }
    public string? AudioObjectKey { get; set; }
    public string? AudioContentType { get; set; }
    public int? AudioDurationMs { get; set; }
    public long? AudioBytes { get; set; }
    public string DiagnosticsJson { get; set; } = "[]";
    public string? ErrorJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

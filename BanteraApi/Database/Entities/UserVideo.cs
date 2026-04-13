namespace BanteraApi.Database.Entities;

public class UserVideo
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string MediaObjectKey { get; set; } = string.Empty;
    public string MediaContentType { get; set; } = "video/mp4";
    public string OriginalFileName { get; set; } = string.Empty;
    public string TranscriptText { get; set; } = string.Empty;
    public string TranscriptLanguage { get; set; } = string.Empty;
    public string TranscriptLanguageCode { get; set; } = string.Empty;
    public string TranscriptCuesJson { get; set; } = "[]";
    public bool IsPublic { get; set; }
    public bool IsAiGenerated { get; set; } = false;
    public bool IsTranscriptionEstimated { get; set; } = false;
    public string? CoverImageObjectKey { get; set; }
    public long FileSizeBytes { get; set; }
    public int DurationMs { get; set; }
    public int? VideoWidth { get; set; }
    public int? VideoHeight { get; set; }
    public DateTime? RemovedFromOwnerListAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

namespace BanteraApi.Database.Entities;

/// <summary>
/// One notable thing that happened in the AI audio pipeline (a failed key, a transcription
/// retry, a fallback, a failed generation, or the quality summary of a finished one).
/// Kept for 90 days so admins can see what fails and refine the pipeline.
/// </summary>
public class AiPipelineEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>info | warning | error</summary>
    public string Severity { get; set; } = "info";
    /// <summary>dialogue | tts | mp3 | transcription | alignment | timing | save | generation | …</summary>
    public string Stage { get; set; } = "";
    /// <summary>Stable machine-readable code, e.g. transcription_incomplete.</summary>
    public string Code { get; set; } = "";
    public Guid? UserId { get; set; }
    public Guid? JobId { get; set; }
    public string? Endpoint { get; set; }
    public string? LanguageCode { get; set; }
    public string? Model { get; set; }
    /// <summary>Masked API key (first 6 + last 4 characters) for key failures.</summary>
    public string? KeyHint { get; set; }
    public string? Message { get; set; }
    public string? DetailJson { get; set; }
    public int? DurationMs { get; set; }
}

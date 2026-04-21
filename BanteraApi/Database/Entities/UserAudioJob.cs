namespace BanteraApi.Database.Entities;

public class UserAudioJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Status { get; set; } = "processing";
    public Guid? VideoId { get; set; }
    public string? LanguageCode { get; set; }
    public string? ScenarioId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

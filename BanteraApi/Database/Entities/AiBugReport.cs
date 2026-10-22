namespace BanteraApi.Database.Entities;

// Only the issue summary and consent excerpt are retained, never the call audio/history.
public sealed class AiBugReport
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Summary { get; set; } = "";
    public string Details { get; set; } = "";
    public string Category { get; set; } = "other";
    public string Source { get; set; } = "voice_message";
    public string ConsentQuote { get; set; } = "";
    public string DedupeKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

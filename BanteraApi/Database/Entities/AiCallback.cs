namespace BanteraApi.Database.Entities;

// Scheduling metadata only. Never stores conversation, audio, transcripts or device learning data.
public sealed class AiCallback
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid PushTokenId { get; set; }
    public UserPushToken PushToken { get; set; } = null!;
    public string RequestKey { get; set; } = "";
    public string TimeZone { get; set; } = "UTC";
    public DateTime DueAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = "scheduled";
}

namespace BanteraApi.Database.Entities;

// Scheduling metadata. Voice reminder audio is a temporary delivery attachment,
// cleared on receipt/cancellation and deleted with the schedule after seven days.
public sealed class AiCallback
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid PushTokenId { get; set; }
    public UserPushToken PushToken { get; set; } = null!;
    public string RequestKey { get; set; } = "";
    public string? Reminder { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public DateTime DueAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = "scheduled";
    public string Delivery { get; set; } = "call";
    public byte[]? Audio { get; set; }
    public string? Transcript { get; set; }
    public string? Language { get; set; }
    public int Attempts { get; set; }
    public DateTime? AttemptAt { get; set; }
}

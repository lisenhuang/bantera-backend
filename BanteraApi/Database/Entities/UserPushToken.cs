namespace BanteraApi.Database.Entities;

public class UserPushToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Platform { get; set; } = "ios";
    public string Token { get; set; } = string.Empty;
    public bool IsSandbox { get; set; }
    public bool SupportsCalls { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    public User User { get; set; } = null!;
}

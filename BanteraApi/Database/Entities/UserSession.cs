namespace BanteraApi.Database.Entities;

public class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string RefreshTokenHash { get; set; } = string.Empty;

    /// <summary>SHA-256 hex (64 chars) of the plain refresh token for indexed lookup. Null for sessions issued before this column existed.</summary>
    public string? RefreshTokenLookup { get; set; }

    public string? DeviceName { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
}

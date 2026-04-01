namespace BanteraApi.Database.Entities;

public class UserIdentity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>apple | google | email</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Apple/Google: sub from ID token. Email: the email address.</summary>
    public string ProviderUserId { get; set; } = string.Empty;

    /// <summary>Email from provider. May be a relay address for Apple.</summary>
    public string? ProviderEmail { get; set; }

    /// <summary>Only set for provider = "email".</summary>
    public string? PasswordHash { get; set; }

    public DateTime? EmailVerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}

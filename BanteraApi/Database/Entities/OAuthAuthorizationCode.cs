namespace BanteraApi.Database.Entities;

/// <summary>
/// One row per authorization request. Created (pending) when the client hits /oauth/authorize —
/// its <see cref="Id"/> is the opaque request_id handed to the consent page. Consent fills in
/// <see cref="CodeHash"/>, <see cref="UserId"/> and <see cref="GrantedScopes"/>; /oauth/token
/// sets <see cref="RedeemedAt"/>.
///
/// Keeping the request and the issued code in one row makes the binding of
/// code ↔ client ↔ redirect_uri ↔ resource ↔ PKCE challenge ↔ admin structural.
/// </summary>
public class OAuthAuthorizationCode
{
    public Guid Id { get; set; }
    public Guid OAuthClientId { get; set; }

    public string RedirectUri { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string RequestedScopes { get; set; } = string.Empty;
    public string? GrantedScopes { get; set; }

    public string CodeChallenge { get; set; } = string.Empty;
    public string CodeChallengeMethod { get; set; } = "S256";

    /// <summary>Client state, echoed verbatim on the redirect back.</summary>
    public string? State { get; set; }

    /// <summary>SHA-256 hex of the authorization code; null until the admin approves.</summary>
    public string? CodeHash { get; set; }

    /// <summary>The admin who approved this request.</summary>
    public Guid? UserId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsentedAt { get; set; }
    public DateTime? DeniedAt { get; set; }
    public DateTime? RedeemedAt { get; set; }

    public OAuthClient? Client { get; set; }
    public User? User { get; set; }
}

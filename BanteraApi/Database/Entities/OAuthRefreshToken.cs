namespace BanteraApi.Database.Entities;

/// <summary>
/// A refresh token issued to an MCP client. Tokens are rotated on every use: the old row is
/// revoked and points at its replacement. Presenting an already-revoked token is treated as
/// theft and revokes the whole <see cref="FamilyId"/> chain (OAuth 2.1 §4.3.1).
/// </summary>
public class OAuthRefreshToken
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 hex of the plaintext token — the indexed lookup key.</summary>
    public string TokenLookup { get; set; } = string.Empty;

    /// <summary>Shared by every token in a rotation chain, so reuse can revoke all of them.</summary>
    public Guid FamilyId { get; set; }

    public Guid OAuthClientId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Provenance: the authorization code this chain originated from.</summary>
    public Guid? AuthorizationCodeId { get; set; }

    public string Scopes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }

    public OAuthClient? Client { get; set; }
    public User? User { get; set; }
}

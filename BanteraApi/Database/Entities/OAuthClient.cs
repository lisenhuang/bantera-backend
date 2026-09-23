namespace BanteraApi.Database.Entities;

/// <summary>
/// An OAuth client registered against the MCP authorization server — either created
/// dynamically (RFC 7591) by an MCP client such as Claude, or seeded from configuration.
/// </summary>
public class OAuthClient
{
    public Guid Id { get; set; }
    public string ClientId { get; set; } = string.Empty;

    /// <summary>BCrypt hash; null for public clients (token_endpoint_auth_method = none).</summary>
    public string? ClientSecretHash { get; set; }

    public string ClientName { get; set; } = string.Empty;

    /// <summary>Registered redirect URIs, stored as a JSON array.</summary>
    public List<string> RedirectUris { get; set; } = [];

    /// <summary>Granted grant types, stored as a JSON array.</summary>
    public List<string> GrantTypes { get; set; } = [];

    public string TokenEndpointAuthMethod { get; set; } = "none";
    public string? ClientUri { get; set; }

    /// <summary>True for clients seeded from configuration; they cannot be replaced by DCR.</summary>
    public bool IsStatic { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public ICollection<OAuthRefreshToken> RefreshTokens { get; set; } = [];
}

namespace BanteraApi.Mcp;

/// <summary>
/// Configuration for the admin MCP server and its OAuth 2.1 authorization server.
/// Bound from the "Mcp" configuration section; production supplies secrets via
/// <c>Mcp__SigningKey</c> style environment variables.
/// </summary>
public class McpSettings
{
    public const string Section = "Mcp";

    /// <summary>OAuth issuer, e.g. https://api.bantera.app (no trailing slash, no path).</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Canonical MCP resource URL, e.g. https://api.bantera.app/mcp. Used as the token audience.</summary>
    public string ResourceUrl { get; set; } = string.Empty;

    /// <summary>Website consent page the backend redirects admins to, e.g. https://bantera.app/dashboard/oauth/authorize.</summary>
    public string ConsentUrl { get; set; } = string.Empty;

    /// <summary>HS256 signing key for MCP access tokens. Must differ from Jwt:Secret.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 60;
    public int RefreshTokenDays { get; set; } = 30;
    public int AuthorizationCodeMinutes { get; set; } = 10;

    /// <summary>
    /// Non-loopback hosts allowed as OAuth redirect targets. Empty means any HTTPS host
    /// (used in development). Loopback http redirects are always allowed (RFC 8252).
    /// </summary>
    public List<string> AllowedRedirectHosts { get; set; } = [];

    /// <summary>Pre-registered clients, for connectors that cannot use dynamic registration.</summary>
    public List<McpStaticClient> StaticClients { get; set; } = [];
}

public class McpStaticClient
{
    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public List<string> RedirectUris { get; set; } = [];
}

using System.Security.Claims;

namespace BanteraApi.Mcp;

/// <summary>
/// Scheme and policy names for the MCP resource server. The MCP bearer scheme is
/// separate from the app's default "Bearer" scheme so that app tokens can never be
/// used on /mcp and MCP tokens can never be used on /api/*.
/// </summary>
public static class McpAuthDefaults
{
    public const string BearerScheme = "McpBearer";
    public const string ReadPolicy = "McpRead";
    public const string WritePolicy = "McpWrite";

    public const string ScopeClaim = "scope";
    public const string ClientIdClaim = "client_id";
}

/// <summary>OAuth scopes exposed by the MCP server. Pure helpers — unit tested without HTTP.</summary>
public static class McpScopes
{
    public const string Read = "mcp:read";
    public const string Write = "mcp:write";

    public static readonly IReadOnlyList<string> All = [Read, Write];

    /// <summary>
    /// Parses a space-delimited scope string into a validated, de-duplicated set.
    /// Returns false when any requested scope is unknown.
    /// </summary>
    public static bool TryParse(string? raw, out HashSet<string> scopes)
    {
        scopes = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!All.Contains(part, StringComparer.Ordinal))
                return false;
            scopes.Add(part);
        }

        return true;
    }

    /// <summary>Normalizes a granted scope set: always includes read, drops unknown values.</summary>
    public static HashSet<string> Normalize(IEnumerable<string>? requested)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { Read };
        foreach (var scope in requested ?? [])
        {
            if (All.Contains(scope, StringComparer.Ordinal))
                result.Add(scope);
        }
        return result;
    }

    /// <summary>Space-joined representation in a stable order (read first).</summary>
    public static string Join(IEnumerable<string> scopes)
    {
        var set = new HashSet<string>(scopes, StringComparer.Ordinal);
        return string.Join(' ', All.Where(set.Contains));
    }

    /// <summary>True when every scope in <paramref name="subset"/> is present in <paramref name="granted"/>.</summary>
    public static bool IsSubset(IEnumerable<string> subset, IEnumerable<string> granted)
    {
        var grantedSet = new HashSet<string>(granted, StringComparer.Ordinal);
        return subset.All(grantedSet.Contains);
    }

    /// <summary>
    /// Reads the scopes from a principal. Handles both a single space-delimited "scope"
    /// claim and multiple repeated claims ("scope" or "scp"), which vary between issuers.
    /// </summary>
    public static HashSet<string> FromPrincipal(ClaimsPrincipal? principal)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        if (principal is null)
            return scopes;

        foreach (var claim in principal.Claims)
        {
            if (claim.Type is not (McpAuthDefaults.ScopeClaim or "scp"))
                continue;

            foreach (var part in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                scopes.Add(part);
        }

        return scopes;
    }

    public static bool Has(ClaimsPrincipal? principal, string scope)
        => FromPrincipal(principal).Contains(scope);
}

namespace BanteraApi.Mcp;

/// <summary>
/// Redirect URI validation and matching.
///
/// Registration accepts HTTPS URIs (optionally restricted to an allow-list of hosts)
/// and loopback HTTP URIs. Matching is exact, except for loopback URIs where the port
/// is ignored — native clients such as Claude Code bind an ephemeral port per session
/// (RFC 8252 §7.3).
/// </summary>
public static class OAuthRedirectUri
{
    public static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "::1"
        || host == "[::1]";

    public static bool IsLoopback(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri.Host);

    /// <summary>
    /// Validates a redirect URI supplied at client-registration time.
    /// Returns null when valid, otherwise a short error description.
    /// </summary>
    public static string? ValidateForRegistration(string? value, IReadOnlyCollection<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Redirect URI must not be empty.";

        if (value.Length > 2000)
            return "Redirect URI is too long.";

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return $"Redirect URI is not an absolute URI: {value}";

        if (!string.IsNullOrEmpty(uri.Fragment))
            return "Redirect URI must not contain a fragment.";

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            if (string.IsNullOrEmpty(uri.Host))
                return "Redirect URI must have a host.";

            if (allowedHosts.Count > 0
                && !allowedHosts.Any(h => h.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)))
            {
                return $"Redirect URI host is not allowed: {uri.Host}";
            }

            return null;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri.Host))
            return null;

        return "Redirect URI must use https, or http with a loopback host.";
    }

    /// <summary>
    /// True when a requested redirect URI matches a registered one. Exact ordinal match,
    /// except that two loopback HTTP URIs match when everything but the port is equal.
    /// </summary>
    public static bool Matches(string registered, string requested)
    {
        if (string.Equals(registered, requested, StringComparison.Ordinal))
            return true;

        if (!Uri.TryCreate(registered, UriKind.Absolute, out var a)
            || !Uri.TryCreate(requested, UriKind.Absolute, out var b))
        {
            return false;
        }

        if (!IsLoopback(a) || !IsLoopback(b))
            return false;

        // Loopback: ignore the port, but the host form must still agree (localhost vs 127.0.0.1
        // are both accepted by Claude Code, so compare them case-insensitively as written).
        return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.AbsolutePath, b.AbsolutePath, StringComparison.Ordinal)
            && string.Equals(a.Query, b.Query, StringComparison.Ordinal);
    }

    /// <summary>Finds the registered URI that matches, or null.</summary>
    public static string? FindMatch(IEnumerable<string> registered, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null;

        return registered.FirstOrDefault(r => Matches(r, requested));
    }

    /// <summary>Host shown on the consent screen so the admin can see where they will be sent.</summary>
    public static string DisplayHost(string redirectUri)
        => Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) ? uri.Host : redirectUri;
}

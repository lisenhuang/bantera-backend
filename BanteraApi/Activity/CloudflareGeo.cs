namespace BanteraApi.Activity;

/// <summary>Visitor location as reported by Cloudflare.</summary>
public sealed record VisitorGeo(string? CountryCode, string? Region, string? City)
{
    public static readonly VisitorGeo None = new(null, null, null);

    public bool IsEmpty => CountryCode is null && Region is null && City is null;
}

/// <summary>
/// Reads Cloudflare's visitor-location request headers.
///
/// <c>CF-IPCountry</c> is sent whenever IP Geolocation is on. <c>cf-region</c> and
/// <c>cf-ipcity</c> only arrive when the "Add visitor location headers" Managed Transform is
/// enabled on the zone. The origin is only reachable through Cloudflare Tunnel, so these
/// headers cannot be supplied by a client directly.
/// </summary>
public static class CloudflareGeo
{
    private const int MaxTextLength = 100;

    public static VisitorGeo FromHeaders(IHeaderDictionary headers)
    {
        var country = NormalizeCountry(headers["CF-IPCountry"].ToString());
        var region = NormalizeText(headers["cf-region"].ToString());
        var city = NormalizeText(headers["cf-ipcity"].ToString());

        return country is null && region is null && city is null
            ? VisitorGeo.None
            : new VisitorGeo(country, region, city);
    }

    /// <summary>
    /// Returns an upper-case ISO alpha-2 code, or null. Cloudflare uses "XX" for unknown
    /// and "T1" for Tor exit nodes; neither is a real country.
    /// </summary>
    public static string? NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var code = value.Trim().ToUpperInvariant();
        if (code.Length != 2 || !char.IsAsciiLetterUpper(code[0]) || !char.IsAsciiLetterUpper(code[1]))
            return null;

        return code is "XX" or "T1" ? null : code;
    }

    public static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim();
        return text.Length > MaxTextLength ? text[..MaxTextLength] : text;
    }
}

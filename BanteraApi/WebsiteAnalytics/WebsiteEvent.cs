namespace BanteraApi.WebsiteAnalytics;

// No account IDs, IP addresses, full referrer URLs, recordings or free-form event payloads.
public sealed class WebsiteEvent
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string LandingPath { get; set; } = "";
    public string Source { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string ReferrerHost { get; set; } = "";
    public string Campaign { get; set; } = "";
    public string Medium { get; set; } = "";
    public string Language { get; set; } = "";
    public string Device { get; set; } = "";
}

public sealed record WebsiteEventInput(Guid Id, Guid SessionId, string? Name, string? Path,
    string? LandingPath, string? ReferrerHost, string? UtmSource, string? UtmMedium,
    string? UtmCampaign, string? Language, string? Device);

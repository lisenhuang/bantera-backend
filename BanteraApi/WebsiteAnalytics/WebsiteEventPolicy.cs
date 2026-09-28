using System.Text.RegularExpressions;

namespace BanteraApi.WebsiteAnalytics;

public static partial class WebsiteEventPolicy
{
    public static readonly string[] EventNames = ["page_view", "lesson_play", "lesson_listened_30s", "download_ios", "download_android"];

    [GeneratedRegex(@"^/(?:learn(?:/[a-z-]+)?|webapp(?:/studio|/shadowing/[0-9a-fA-F-]{36}|/[0-9a-fA-F-]{36})?|download|faq|support|privacy|delete-account)?$")]
    private static partial Regex PublicPath();
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$")]
    private static partial Regex CampaignToken();
    [GeneratedRegex(@"^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex LanguageToken();

    public static string? CleanPath(string? value)
    {
        if (value is null || value.Length > 200) return null;
        var path = value.Split('?', '#')[0].TrimEnd('/');
        if (path == "") path = "/";
        return PublicPath().IsMatch(path) ? path : null;
    }

    private static string Token(string? value) => value is not null && CampaignToken().IsMatch(value) ? value : "";
    public static string CleanHost(string? value)
    {
        if (value is null || value.Length > 253 || value.Contains('/') || value.Contains('@') || value.Contains(':')) return "";
        return Uri.CheckHostName(value) == UriHostNameType.Dns ? value.ToLowerInvariant().TrimEnd('.') : "";
    }
    private static bool Domain(string host, string domain) => host == domain || host.EndsWith('.' + domain, StringComparison.Ordinal);

    public static (string Source, string Evidence) Attribution(string? utmSource, string host)
    {
        var source = Token(utmSource).ToLowerInvariant();
        if (source != "") return (source, "campaign tag");
        if (host == "" || Domain(host, "bantera.app") || host == "localhost") return ("Direct / unknown", "unavailable");
        foreach (var domain in new[] { "chatgpt.com", "chat.openai.com", "perplexity.ai", "claude.ai", "gemini.google.com", "copilot.microsoft.com", "bing.com", "duckduckgo.com", "yahoo.com", "baidu.com" })
            if (Domain(host, domain)) return (domain, "referrer");
        // Match real Google country domains, never google.example.com or google.com.evil.test.
        if (GoogleHost().IsMatch(host)) return ("google", "referrer");
        return (host, "referrer");
    }
    [GeneratedRegex(@"^(?:www\.)?google\.(?:com|co\.(?:uk|nz|in|jp|za|kr)|com\.(?:au|br|mx|sg|tw|hk|ar)|ca|de|fr|es|it|nl|pl|pt|ie|ch|at|se|no|dk|fi|be)$")]
    private static partial Regex GoogleHost();

    public static WebsiteEvent? Normalize(WebsiteEventInput input, DateTime now)
    {
        var path = CleanPath(input.Path);
        var landing = CleanPath(input.LandingPath);
        if (input.Id == Guid.Empty || input.SessionId == Guid.Empty || path is null || landing is null || !EventNames.Contains(input.Name)) return null;
        var host = CleanHost(input.ReferrerHost);
        var (source, evidence) = Attribution(input.UtmSource, host);
        return new WebsiteEvent
        {
            Id = input.Id, SessionId = input.SessionId, ReceivedAt = now, Name = input.Name!, Path = path,
            LandingPath = landing, Source = source, Evidence = evidence, ReferrerHost = host,
            Campaign = Token(input.UtmCampaign), Medium = Token(input.UtmMedium),
            Language = input.Language is not null && LanguageToken().IsMatch(input.Language) ? input.Language.ToLowerInvariant() : "",
            Device = input.Device is "mobile" or "tablet" or "desktop" ? input.Device : "unknown",
        };
    }
}

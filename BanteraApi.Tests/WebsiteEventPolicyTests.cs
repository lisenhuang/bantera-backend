using BanteraApi.WebsiteAnalytics;
using Xunit;

namespace BanteraApi.Tests;
public sealed class WebsiteEventPolicyTests
{
    [Theory]
    [InlineData("/dashboard/users", null)]
    [InlineData("/api/auth", null)]
    [InlineData("/webapp?search=private+text", "/webapp")]
    [InlineData("/learn/spanish#practice", "/learn/spanish")]
    [InlineData("/unknown/personal-name", null)]
    public void OnlyPublicPathsAreStored(string raw, string? expected) => Assert.Equal(expected, WebsiteEventPolicy.CleanPath(raw));

    [Theory]
    [InlineData(null, "www.google.co.nz", "google", "referrer")]
    [InlineData(null, "chatgpt.com", "chatgpt.com", "referrer")]
    [InlineData("chatgpt.com", "", "chatgpt.com", "campaign tag")]
    [InlineData(null, "www.bantera.app", "Direct / unknown", "unavailable")]
    [InlineData(null, "", "Direct / unknown", "unavailable")]
    [InlineData(null, "google.com.attacker.test", "google.com.attacker.test", "referrer")]
    [InlineData("private message with spaces", "perplexity.ai", "perplexity.ai", "referrer")]
    public void AttributionDistinguishesEvidence(string? tag, string host, string source, string evidence)
        => Assert.Equal((source, evidence), WebsiteEventPolicy.Attribution(tag, host));

    [Theory]
    [InlineData("https://google.com/private?q=secret")]
    [InlineData("person@example.com")]
    [InlineData("127.0.0.1")]
    public void ReferrersRejectUrlsEmailsAndAddresses(string value) => Assert.Equal("", WebsiteEventPolicy.CleanHost(value));

    [Fact]
    public void IngestionDropsUnsupportedFieldsAndRejectsUnknownEvents()
    {
        var input = new WebsiteEventInput(Guid.NewGuid(), Guid.NewGuid(), "page_view", "/learn/french?email=test", "/",
            "https://example.com/private", null, "email", "contains personal text", "fr-FR", "browser fingerprint");
        var row = WebsiteEventPolicy.Normalize(input, DateTime.UtcNow)!;
        Assert.Equal("/learn/french", row.Path);
        Assert.Equal("", row.Campaign);
        Assert.Equal("", row.ReferrerHost);
        Assert.Equal("fr-fr", row.Language);
        Assert.Equal("unknown", row.Device);
        Assert.Null(WebsiteEventPolicy.Normalize(input with { Name = "recording_uploaded" }, DateTime.UtcNow));
        Assert.Null(WebsiteEventPolicy.Normalize(input with { Id = Guid.Empty }, DateTime.UtcNow));
    }
}

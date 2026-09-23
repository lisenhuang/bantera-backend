using BanteraApi.Mcp;
using Xunit;

namespace BanteraApi.Tests;

public class OAuthRedirectUriTests
{
    private static readonly string[] NoHostRestriction = [];
    private static readonly string[] ClaudeOnly = ["claude.ai"];

    [Fact]
    public void Matches_ExactHttpsUriMatches()
    {
        const string uri = "https://claude.ai/api/mcp/auth_callback";
        Assert.True(OAuthRedirectUri.Matches(uri, uri));
    }

    [Fact]
    public void Matches_HttpsWithDifferentPathDoesNotMatch()
    {
        Assert.False(OAuthRedirectUri.Matches(
            "https://claude.ai/api/mcp/auth_callback",
            "https://claude.ai/api/mcp/other"));
    }

    [Fact]
    public void Matches_LoopbackIgnoresPort()
    {
        // Claude Code binds an ephemeral port per session (RFC 8252 section 7.3).
        Assert.True(OAuthRedirectUri.Matches("http://localhost:9000/callback", "http://localhost:51234/callback"));
        Assert.True(OAuthRedirectUri.Matches("http://127.0.0.1:1/callback", "http://127.0.0.1:65535/callback"));
    }

    [Fact]
    public void Matches_LoopbackStillComparesPath()
    {
        Assert.False(OAuthRedirectUri.Matches("http://localhost:9000/callback", "http://localhost:9000/evil"));
    }

    [Fact]
    public void Matches_DifferentLoopbackHostFormsDoNotCrossMatch()
    {
        Assert.False(OAuthRedirectUri.Matches("http://localhost:9000/callback", "http://127.0.0.1:9000/callback"));
    }

    [Fact]
    public void Matches_HttpsPortIsNotIgnored()
    {
        Assert.False(OAuthRedirectUri.Matches("https://claude.ai/cb", "https://claude.ai:8443/cb"));
    }

    [Fact]
    public void ValidateForRegistration_AcceptsHttpsAndLoopback()
    {
        Assert.Null(OAuthRedirectUri.ValidateForRegistration("https://claude.ai/api/mcp/auth_callback", NoHostRestriction));
        Assert.Null(OAuthRedirectUri.ValidateForRegistration("http://localhost:1234/callback", NoHostRestriction));
        Assert.Null(OAuthRedirectUri.ValidateForRegistration("http://127.0.0.1:1234/callback", NoHostRestriction));
    }

    [Fact]
    public void ValidateForRegistration_RejectsPlainHttpOnRemoteHost()
    {
        Assert.NotNull(OAuthRedirectUri.ValidateForRegistration("http://claude.ai/callback", NoHostRestriction));
    }

    [Fact]
    public void ValidateForRegistration_RejectsFragmentsAndCustomSchemes()
    {
        Assert.NotNull(OAuthRedirectUri.ValidateForRegistration("https://claude.ai/cb#frag", NoHostRestriction));
        Assert.NotNull(OAuthRedirectUri.ValidateForRegistration("myapp://callback", NoHostRestriction));
        Assert.NotNull(OAuthRedirectUri.ValidateForRegistration("not-a-uri", NoHostRestriction));
        Assert.NotNull(OAuthRedirectUri.ValidateForRegistration("", NoHostRestriction));
    }

    [Fact]
    public void ValidateForRegistration_EnforcesHostAllowListForHttps()
    {
        Assert.Null(OAuthRedirectUri.ValidateForRegistration("https://claude.ai/cb", ClaudeOnly));
        Assert.NotNull(OAuthRedirectUri.ValidateForRegistration("https://evil.example/cb", ClaudeOnly));
    }

    [Fact]
    public void ValidateForRegistration_LoopbackBypassesHostAllowList()
    {
        // Native clients cannot register a public host, so loopback stays allowed.
        Assert.Null(OAuthRedirectUri.ValidateForRegistration("http://localhost:7777/callback", ClaudeOnly));
    }

    [Fact]
    public void FindMatch_ReturnsTheMatchingRegisteredUri()
    {
        string[] registered = ["https://claude.ai/cb", "http://localhost:9000/callback"];

        Assert.Equal("http://localhost:9000/callback",
            OAuthRedirectUri.FindMatch(registered, "http://localhost:44444/callback"));
        Assert.Null(OAuthRedirectUri.FindMatch(registered, "https://evil.example/cb"));
        Assert.Null(OAuthRedirectUri.FindMatch(registered, null));
    }

    [Fact]
    public void DisplayHost_ReturnsHostForConsentScreen()
    {
        Assert.Equal("claude.ai", OAuthRedirectUri.DisplayHost("https://claude.ai/api/mcp/auth_callback"));
        Assert.Equal("localhost", OAuthRedirectUri.DisplayHost("http://localhost:1234/callback"));
    }
}

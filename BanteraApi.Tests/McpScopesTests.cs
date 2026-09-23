using System.Security.Claims;
using BanteraApi.Mcp;
using Xunit;

namespace BanteraApi.Tests;

public class McpScopesTests
{
    [Fact]
    public void TryParse_AcceptsKnownScopes()
    {
        Assert.True(McpScopes.TryParse("mcp:read mcp:write", out var scopes));
        Assert.Equal(2, scopes.Count);
        Assert.Contains(McpScopes.Read, scopes);
        Assert.Contains(McpScopes.Write, scopes);
    }

    [Fact]
    public void TryParse_TreatsEmptyAsNoScopes()
    {
        Assert.True(McpScopes.TryParse(null, out var a));
        Assert.Empty(a);

        Assert.True(McpScopes.TryParse("   ", out var b));
        Assert.Empty(b);
    }

    [Fact]
    public void TryParse_RejectsUnknownScope()
    {
        Assert.False(McpScopes.TryParse("mcp:read admin:everything", out _));
    }

    [Fact]
    public void TryParse_DeduplicatesAndIgnoresExtraWhitespace()
    {
        Assert.True(McpScopes.TryParse("mcp:read  mcp:read", out var scopes));
        Assert.Single(scopes);
    }

    [Fact]
    public void Normalize_AlwaysIncludesReadAndDropsUnknown()
    {
        var scopes = McpScopes.Normalize(["mcp:write", "bogus"]);

        Assert.Contains(McpScopes.Read, scopes);
        Assert.Contains(McpScopes.Write, scopes);
        Assert.DoesNotContain("bogus", scopes);
    }

    [Fact]
    public void Normalize_OfNothingStillGrantsRead()
    {
        Assert.Equal([McpScopes.Read], McpScopes.Normalize(null));
    }

    [Fact]
    public void Join_UsesStableOrder()
    {
        Assert.Equal("mcp:read mcp:write", McpScopes.Join(["mcp:write", "mcp:read"]));
        Assert.Equal("mcp:read", McpScopes.Join(["mcp:read"]));
    }

    [Fact]
    public void IsSubset_ChecksContainment()
    {
        Assert.True(McpScopes.IsSubset(["mcp:read"], ["mcp:read", "mcp:write"]));
        Assert.False(McpScopes.IsSubset(["mcp:write"], ["mcp:read"]));
    }

    [Fact]
    public void FromPrincipal_ReadsSpaceDelimitedScopeClaim()
    {
        var principal = PrincipalWith(new Claim("scope", "mcp:read mcp:write"));

        var scopes = McpScopes.FromPrincipal(principal);

        Assert.Equal(2, scopes.Count);
        Assert.True(McpScopes.Has(principal, McpScopes.Write));
    }

    [Fact]
    public void FromPrincipal_ReadsRepeatedClaims()
    {
        var principal = PrincipalWith(new Claim("scope", "mcp:read"), new Claim("scp", "mcp:write"));

        Assert.True(McpScopes.Has(principal, McpScopes.Read));
        Assert.True(McpScopes.Has(principal, McpScopes.Write));
    }

    [Fact]
    public void Has_IsFalseWhenScopeMissingOrPrincipalNull()
    {
        var readOnly = PrincipalWith(new Claim("scope", "mcp:read"));

        Assert.False(McpScopes.Has(readOnly, McpScopes.Write));
        Assert.False(McpScopes.Has(null, McpScopes.Read));
    }

    private static ClaimsPrincipal PrincipalWith(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "test"));
}

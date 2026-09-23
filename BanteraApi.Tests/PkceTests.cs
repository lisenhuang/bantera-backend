using BanteraApi.Mcp;
using Xunit;

namespace BanteraApi.Tests;

public class PkceTests
{
    // RFC 7636 Appendix B reference vector.
    private const string RfcVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string RfcChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void ComputeS256_MatchesRfcVector()
    {
        Assert.Equal(RfcChallenge, Pkce.ComputeS256(RfcVerifier));
    }

    [Fact]
    public void VerifyS256_AcceptsMatchingVerifier()
    {
        Assert.True(Pkce.VerifyS256(RfcVerifier, RfcChallenge));
    }

    [Fact]
    public void VerifyS256_RejectsWrongVerifier()
    {
        var other = new string('a', 43);
        Assert.False(Pkce.VerifyS256(other, RfcChallenge));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tooshort")]
    public void VerifyS256_RejectsMalformedVerifier(string? verifier)
    {
        Assert.False(Pkce.VerifyS256(verifier, RfcChallenge));
    }

    [Fact]
    public void IsValidVerifier_EnforcesLengthBounds()
    {
        Assert.False(Pkce.IsValidVerifier(new string('a', 42)));
        Assert.True(Pkce.IsValidVerifier(new string('a', 43)));
        Assert.True(Pkce.IsValidVerifier(new string('a', 128)));
        Assert.False(Pkce.IsValidVerifier(new string('a', 129)));
    }

    [Fact]
    public void IsValidVerifier_RejectsCharactersOutsideUnreservedSet()
    {
        Assert.False(Pkce.IsValidVerifier(new string('a', 42) + "+"));
        Assert.False(Pkce.IsValidVerifier(new string('a', 42) + "/"));
        Assert.True(Pkce.IsValidVerifier(new string('a', 39) + "-._~"));
    }

    [Fact]
    public void IsValidChallenge_RequiresBase64UrlOf43Chars()
    {
        Assert.True(Pkce.IsValidChallenge(RfcChallenge));
        Assert.False(Pkce.IsValidChallenge(new string('a', 42)));
        Assert.False(Pkce.IsValidChallenge(new string('a', 44)));
        Assert.False(Pkce.IsValidChallenge(new string('a', 42) + "="));
        Assert.False(Pkce.IsValidChallenge(new string('a', 42) + "+"));
    }

    [Fact]
    public void RandomToken_IsUrlSafeAndUnique()
    {
        var a = Pkce.RandomToken(32);
        var b = Pkce.RandomToken(32);

        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
    }
}

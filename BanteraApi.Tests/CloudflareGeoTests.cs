using BanteraApi.Activity;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BanteraApi.Tests;

public class CloudflareGeoTests
{
    [Fact]
    public void FromHeaders_ReadsCountryRegionAndCity()
    {
        var headers = new HeaderDictionary
        {
            ["CF-IPCountry"] = "nz",
            ["cf-region"] = "Auckland",
            ["cf-ipcity"] = "Auckland",
        };

        var geo = CloudflareGeo.FromHeaders(headers);

        Assert.Equal("NZ", geo.CountryCode);
        Assert.Equal("Auckland", geo.Region);
        Assert.Equal("Auckland", geo.City);
    }

    [Fact]
    public void FromHeaders_KeepsNonAsciiCityNames()
    {
        var headers = new HeaderDictionary { ["CF-IPCountry"] = "CH", ["cf-ipcity"] = "Zürich" };

        Assert.Equal("Zürich", CloudflareGeo.FromHeaders(headers).City);
    }

    [Fact]
    public void FromHeaders_ReturnsNoneWhenAbsent()
    {
        var geo = CloudflareGeo.FromHeaders(new HeaderDictionary());

        Assert.True(geo.IsEmpty);
    }

    [Theory]
    [InlineData("XX")]   // Cloudflare: unknown
    [InlineData("T1")]   // Cloudflare: Tor exit node
    [InlineData("USA")]
    [InlineData("1A")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeCountry_RejectsNonCountries(string? value)
    {
        Assert.Null(CloudflareGeo.NormalizeCountry(value));
    }

    [Fact]
    public void NormalizeText_TrimsAndCapsLength()
    {
        Assert.Equal("Tokyo", CloudflareGeo.NormalizeText("  Tokyo "));
        Assert.Null(CloudflareGeo.NormalizeText("   "));
        Assert.Equal(100, CloudflareGeo.NormalizeText(new string('a', 300))!.Length);
    }
}

public class LanguageFlagsTests
{
    [Theory]
    [InlineData("en-NZ", "🇳🇿")]
    [InlineData("en-US", "🇺🇸")]
    [InlineData("zh-TW", "🇹🇼")]
    [InlineData("ja", "🇯🇵")]
    public void For_UsesCatalogFlagForExactCode(string code, string flag)
    {
        Assert.Equal(flag, LanguageFlags.For(code));
    }

    [Fact]
    public void For_IsCaseAndSeparatorInsensitive()
    {
        Assert.Equal(LanguageFlags.For("en-US"), LanguageFlags.For("EN_us"));
    }

    [Fact]
    public void For_FallsBackToLanguageFamily()
    {
        // en-JM is not in either catalog, so the "en" family flag is used.
        Assert.Equal(LanguageFlags.For("en"), LanguageFlags.For("en-JM"));
    }

    [Fact]
    public void For_FallsBackToRegionSubtag()
    {
        // No catalog entry for Galician at all, so the region builds the flag.
        Assert.Equal("🇪🇸", LanguageFlags.For("gl-ES"));
    }

    [Fact]
    public void For_UsesGlobeWhenNothingMatches()
    {
        Assert.Equal(LanguageFlags.Globe, LanguageFlags.For("gl"));
        Assert.Equal(LanguageFlags.Globe, LanguageFlags.For(null));
    }

    [Theory]
    [InlineData("NZ", "🇳🇿")]
    [InlineData("gb", "🇬🇧")]
    public void FromCountryCode_BuildsRegionalIndicators(string code, string flag)
    {
        Assert.Equal(flag, LanguageFlags.FromCountryCode(code));
    }

    [Theory]
    [InlineData("USA")]
    [InlineData("1A")]
    [InlineData(null)]
    public void FromCountryCode_RejectsInvalidCodes(string? code)
    {
        Assert.Null(LanguageFlags.FromCountryCode(code));
    }
}

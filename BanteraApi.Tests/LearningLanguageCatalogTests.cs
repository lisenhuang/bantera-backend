using BanteraApi;
using Xunit;

namespace BanteraApi.Tests;

public class LearningLanguageCatalogTests
{
    [Fact]
    public void Items_UsesCuratedGlobalPopularityOrder()
    {
        var identifiers = LearningLanguageCatalog.Items
            .Select(item => item.Identifier)
            .ToArray();

        Assert.Equal(
            [
                "en-US",
                "en-GB",
                "en-AU",
                "en-CA",
                "en-IN",
                "en-NZ",
                "en-IE",
                "en-SG",
                "en-ZA",
                "en-PH",
                "en-AE",
                "en-ID",
                "en-SA",
                "es-MX",
                "es-ES",
                "es-419",
                "es-US",
                "es-CO",
                "es-CL",
                "fr-FR",
                "fr-CA",
                "fr-BE",
                "fr-CH",
                "de-DE",
                "de-AT",
                "de-CH",
                "it-IT",
                "it-CH",
            ],
            identifiers);
    }
}

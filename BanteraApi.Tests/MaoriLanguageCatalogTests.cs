using BanteraApi;
using BanteraApi.Admin;
using Xunit;
namespace BanteraApi.Tests;
public class MaoriLanguageCatalogTests
{
    [Fact]
    public void MaoriIsAvailableOnlyForPrivateAudioTests()
    {
        var language = Assert.IsType<LearningLanguageItem>(AdminAudioTestLanguages.Find("mi-NZ"));
        Assert.Equal("Māori (New Zealand)", language.DisplayName);
        Assert.True(AdminAudioTestLanguages.Supports(language.Identifier));
        Assert.False(LearningLanguageCatalog.SupportsAudioDialogues(language.Identifier));
        Assert.DoesNotContain(LearningLanguageCatalog.Items, item => item.Identifier == language.Identifier);
        Assert.Null(AdminAudioTestLanguages.Find("unknown"));
    }

    [Fact]
    public void ExistingAudioTestLanguagesRemainAvailable()
    {
        foreach (var language in LearningLanguageCatalog.Items)
        {
            Assert.Equal(language, AdminAudioTestLanguages.Find(language.Identifier));
            Assert.True(AdminAudioTestLanguages.Supports(language.Identifier));
        }
    }

    [Fact]
    public void RetiredMaoriIsAbsentAndBothLearningCantoneseOptionsRemain()
    {
        Assert.DoesNotContain(LearningLanguageCatalog.Items, x => x.Identifier.StartsWith("mi", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(LearningLanguageCatalog.Items, x => x.Identifier == "yue-CN");
        Assert.Contains(LearningLanguageCatalog.Items, x => x.Identifier == "zh-HK");
    }
    [Theory]
    [InlineData("mi-NZ", false)]
    [InlineData("MI_nz", false)]
    [InlineData("mi", false)]
    [InlineData("en-NZ", true)]
    [InlineData("yue-CN", true)]
    [InlineData("zh-HK", true)]
    public void StaleClientsCannotGenerateRetiredAudio(string locale, bool supported)
        => Assert.Equal(supported, LearningLanguageCatalog.SupportsAudioDialogues(locale));
}

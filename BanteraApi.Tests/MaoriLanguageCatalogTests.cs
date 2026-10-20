using BanteraApi;
using Xunit;
namespace BanteraApi.Tests;
public class MaoriLanguageCatalogTests
{
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

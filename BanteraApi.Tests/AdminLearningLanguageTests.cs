using Xunit;
namespace BanteraApi.Tests;
public class AdminLearningLanguageTests
{
    [Theory]
    [InlineData("mi-NZ")]
    [InlineData("MI_nz")]
    [InlineData("mi")]
    public void MaoriProfileSelectionIsAdminLearningOnly(string locale)
    {
        Assert.False(LearningLanguageCatalog.CanSelectProfileLanguage(locale, false, false));
        Assert.False(LearningLanguageCatalog.CanSelectProfileLanguage(locale, true, true));
        Assert.True(LearningLanguageCatalog.CanSelectProfileLanguage(locale, false, true));
        Assert.DoesNotContain(LearningLanguageCatalog.Items, x => LearningLanguageCatalog.IsMaori(x.Identifier));
    }
    [Fact]
    public void MaoriPracticeIsAdminOnlyAndLongDialogueSupportIsUnchanged()
    {
        Assert.Equal("mi-NZ", LearningLanguageCatalog.ResolvePracticeLocale("MI_nz", true));
        Assert.Null(LearningLanguageCatalog.ResolvePracticeLocale("mi-NZ", false));
        Assert.Equal("ja-JP", LearningLanguageCatalog.ResolvePracticeLocale("ja-JP", false));
        Assert.False(LearningLanguageCatalog.SupportsAudioDialogues("mi-NZ"));
    }
}

using BanteraApi;
using Xunit;

namespace BanteraApi.Tests;

public class LearningLanguageCatalogTests
{
    [Fact]
    public void Items_KeepsTheOriginalOrderFirst()
    {
        var identifiers = LearningLanguageCatalog.Items
            .Select(item => item.Identifier)
            .ToArray();

        string[] original =
        [
            "en-US", "en-GB", "en-AU", "en-CA", "en-IN", "en-NZ", "en-IE", "en-SG", "en-ZA",
            "en-PH", "en-AE", "en-ID", "en-SA",
            "es-MX", "es-ES", "es-419", "es-US", "es-CO", "es-CL",
            "fr-FR", "fr-CA", "fr-BE", "fr-CH",
            "de-DE", "de-AT", "de-CH",
            "it-IT", "it-CH",
        ];
        Assert.Equal(original, identifiers.Take(original.Length));
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    [InlineData("yue-CN")]
    [InlineData("ja-JP")]
    [InlineData("ko-KR")]
    [InlineData("pt-BR")]
    [InlineData("ar-SA")]
    [InlineData("hi-IN")]
    [InlineData("ru-RU")]
    public void Items_IncludesLanguagesBeyondTheOriginalFive(string identifier)
    {
        Assert.Contains(LearningLanguageCatalog.Items, item => item.Identifier == identifier);
    }

    [Fact]
    public void Items_HaveUniqueIdentifiersAndFlags()
    {
        var items = LearningLanguageCatalog.Items;
        Assert.Equal(items.Count, items.Select(i => i.Identifier).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.FlagEmoji)));
    }

    [Theory]
    [InlineData("zh", "zh")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("zh-TW", "zh-tw")]
    [InlineData("zh-HK", "zh-hk")]
    [InlineData("en-GB", "en")]
    public void ChatMatchKey_GroupsMainlandMandarinWithBareZh(string code, string expected)
    {
        Assert.Equal(expected, BanteraApi.Chat.ChatLanguageResolver.Resolve(code)!.MatchKey);
    }
}

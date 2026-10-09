using BanteraApi.Gemini;
using Xunit;
namespace BanteraApi.Tests;
public class AiSearchPolicyTests
{
    [Theory]
    [InlineData("gemini-2.5-flash", true)]
    [InlineData("gemini-2.5-pro", false)]
    [InlineData("gemini-flash-latest", false)]
    [InlineData("gemini-2.5-flash-lite", false)]
    [InlineData("gemini-3.8-flash", false)]
    [InlineData("chatgpt/account-model", true)]
    public void SearchAllowsOnlyApprovedGeminiOrGpt(string model, bool allowed) =>
        Assert.Equal(allowed, AiSearchPolicy.AllowsModel(model));

    [Theory]
    [InlineData("")]
    [InlineData("AQ")]
    [InlineData("AIza")]
    public void LegacyPrefixCannotRelaxTheSearchKeyRestriction(string prefix)
    {
        Assert.Equal(["AIzaSy-valid"], GeminiService.SelectKeys(["AQ-other", "AIza-wrong", "AIzaSy-valid"], true, prefix));
        Assert.Empty(GeminiService.SelectKeys(["AQ-other"], true, prefix));
        Assert.Equal(["AQ-other"], GeminiService.SelectKeys(["AQ-other"], false, prefix));
    }

    [Fact]
    public void OldUnsupportedSearchChoicesCannotExecuteAndTextChoicesStayIntact()
    {
        var original = new AiModelSelection("gemini-2.5-pro", "tts", "gemini-3.8-flash", null,
            SearchModel: "gemini-flash-latest", FallbackSearchModel: "gemini-2.5-pro", SearchReasoning: "max");
        var effective = AiSearchPolicy.Apply(original);
        Assert.Equal("gemini-2.5-flash", effective.SearchModel);
        Assert.Null(effective.FallbackSearchModel); Assert.Null(effective.SearchReasoning);
        Assert.Equal(original.TextModel, effective.TextModel); Assert.Equal(original.FallbackTextModel, effective.FallbackTextModel);
        var gpt = original with { SearchModel = "chatgpt/account-model", SearchReasoning = "max", FallbackSearchModel = "gemini-2.5-flash" };
        Assert.Equal(gpt, AiSearchPolicy.Apply(gpt));
    }
}

using System.Text.Json;
using BanteraApi.Admin;
using BanteraApi.Gemini;
using BanteraApi.OpenAi;
using Xunit;
namespace BanteraApi.Tests;
public class TextProviderTests
{
    [Fact]
    public void ReasoningFollowsSelectedModelAndSearchIsIndependent()
    {
        ChatGptModel[] models = [new("alpha", "Alpha", ["low", "max"], "low"), new("beta", "Beta", ["medium"], "medium")];
        Assert.True(AiSettingsEndpoints.ValidReasoning("chatgpt/alpha", "max", models));
        Assert.False(AiSettingsEndpoints.ValidReasoning("chatgpt/beta", "max", models));
        Assert.False(AiSettingsEndpoints.ValidReasoning("gemini-flash", "max", models));
        Assert.True(AiSettingsEndpoints.ValidReasoning("gemini-flash", null, models));
        var settings = new AiModelSelection("chatgpt/alpha", "tts", "chatgpt/beta", null, "low", "medium", "chatgpt/alpha", "gemini-flash", "max");
        Assert.Equal("low", GeminiService.ReasoningFor(settings, "chatgpt/alpha"));
        Assert.Equal("max", GeminiService.ReasoningFor(settings, "chatgpt/alpha", true));
        Assert.Equal("medium", GeminiService.ReasoningFor(settings, "chatgpt/beta"));
        Assert.Null(GeminiService.ReasoningFor(settings, "gemini-flash", true));
    }

    [Fact]
    public void TextProviderPreservesSystemTaskAndStructuredOutputSchema()
    {
        using var json = JsonDocument.Parse("""{"systemInstruction":{"parts":[{"text":"Summarise privately"}]},"contents":[{"parts":[{"text":"Recent history"},{"text":"Latest message"}]}],"generationConfig":{"responseSchema":{"type":"OBJECT","required":["summary"]}}}""");
        var (prompt, instructions) = GeminiService.TextProviderPrompt(json.RootElement);
        Assert.Contains("Recent history", prompt); Assert.Contains("Latest message", prompt);
        Assert.StartsWith("Summarise privately", instructions); Assert.Contains("required", instructions);
    }
}

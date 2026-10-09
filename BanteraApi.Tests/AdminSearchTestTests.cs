using System.Text.Json;
using BanteraApi.Admin;
using BanteraApi.Gemini;
using Xunit;

namespace BanteraApi.Tests;
public class AdminSearchTestTests
{
    private static SearchTestAnswer Parse(string json) => GeminiService.ParseSearchTest(JsonDocument.Parse(json).RootElement);
    [Fact] public void AnswerWithoutSearchEvidenceDoesNotPass()
    {
        var answer = Parse("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"A plausible answer."}]}}]}""");
        Assert.False(answer.Verified); Assert.Empty(answer.Sources);
    }
    [Fact] public void GroundedAnswerHasSafeDeduplicatedSourcesAndSkipsThoughts()
    {
        var answer = Parse("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"thought":true,"text":"private thought"},{"text":"Verified answer."}]},"groundingMetadata":{"webSearchQueries":["test"],"groundingChunks":[{"web":{"uri":"javascript:alert(1)","title":"bad"}},{"web":{"uri":"https://example.com/news","title":"News"}},{"web":{"uri":"https://example.com/news","title":"duplicate"}}]}}]}""");
        Assert.True(answer.Verified); Assert.Single(answer.Sources); Assert.Equal("Verified answer.",answer.Text);
        Assert.Equal("https://example.com/news",answer.Sources[0].Url);
    }
    [Theory]
    [InlineData("{\"candidates\":[]}")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"MAX_TOKENS\",\"content\":{\"parts\":[{\"text\":\"partial\"}]}}]}")]
    public void IncompleteResponseIsNotSuccess(string json) => Assert.Throws<InvalidDataException>(() => Parse(json));
    [Fact] public void QueryBoundsAreEnforced()
    {
        Assert.False(AiSearchTestEndpoint.Valid(new(null))); Assert.False(AiSearchTestEndpoint.Valid(new("  a  ")));
        Assert.False(AiSearchTestEndpoint.Valid(new(new string('a',1001)))); Assert.True(AiSearchTestEndpoint.Valid(new("today's news")));
    }
}

using System.Text.Json;
using BanteraApi.Chat.Ai;
using Xunit;
namespace BanteraApi.Tests;
public class AiWebSearchTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    [Theory]
    [InlineData("gemini-3.8-live")]
    [InlineData("gemini-3.8-live-extended-thinking")]
    [InlineData("gemini-3.1-flash-live-preview")]
    [InlineData("gemini-2.5-flash-native-audio-latest")]
    public void SearchIsOptInAndUsesCompatibleToolDeclaration(string model)
    {
        Assert.DoesNotContain("search_web", JsonSerializer.Serialize(GeminiLiveService.Setup(model, "prompt", true)));
        var setup = JsonSerializer.SerializeToElement(GeminiLiveService.Setup(model, "prompt", true, deviceWebSearch: true));
        Assert.Contains("search_web", setup.ToString());
        Assert.Contains("untrusted excerpts", setup.ToString());
        Assert.False(AiClientMetadata.Read(null).DeviceWebSearch);
        Assert.True(AiClientMetadata.Read("{\"deviceWebSearch\":true}").DeviceWebSearch);
    }
    [Fact]
    public async Task VoiceSearchIsRelayedAndOnlyMatchingDeviceResultCompletesIt()
    {
        var bridge = new AiVoiceDeviceTools();
        object? frame = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var pending = bridge.InvokeAsync(Json("""{"id":"one","name":"search_web","args":{"query":"Auckland weather"}}"""), value => { frame = value; return Task.CompletedTask; }, timeout.Token);
        Assert.Contains("toolCall", JsonSerializer.Serialize(frame));
        Assert.False(pending.IsCompleted);
        Assert.True(bridge.Accept(Json("""[{"id":"late","name":"search_web","response":{"result":{"unavailable":true}}}]""")));
        Assert.False(pending.IsCompleted);
        Assert.True(bridge.Accept(Json("""[{"id":"one","name":"search_web","response":{"result":{"results":[{"title":"Weather","url":"https://example.com"}]}}}]""")));
        Assert.Contains("Weather", (await pending).ToString());
    }
    [Fact]
    public void CancelledCallSearchCannotSendAStaleToolResponseToModel()
    {
        var pending = new AiPendingTools();
        pending.Register(Json("""[{"id":"one","name":"search_web"}]"""));
        pending.Cancel(Json("""["one"]"""));
        Assert.True(pending.TryAccept(Json("""[{"id":"one","name":"search_web","response":{"result":{}}}]"""), out var accepted));
        Assert.Equal(0, accepted.GetArrayLength());
        Assert.False(pending.Accept(Json("""[{"id":"unsolicited","name":"search_web","response":{}}]""")));
    }
}

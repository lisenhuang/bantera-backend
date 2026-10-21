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
    public void PhoneCallbackRedirectsSearchButInAppCallsKeepDeviceTools(string model)
    {
        var user = new BanteraApi.Database.Entities.User { LearningLanguage = "en-NZ" };
        var metadata = new AiClientMetadata(new("UTC", 0), null, true, DeviceWebSearch: true,
            ConversationId: Guid.NewGuid().ToString(), DeviceVocabulary: true, DeviceEmojis: true);
        // The server's phone-call restriction wins over the app advertising search.
        var phone = metadata with { PhoneCallback = true };
        var setup = GeminiLiveService.SessionSetup(model, user, false, [], phone, "Puck", null, "phone", null)["setup"]!;
        var declarations = setup["tools"]![0]!["functionDeclarations"]!.AsArray()
            .Select(tool => tool!["name"]!.GetValue<string>()).ToArray();
        Assert.DoesNotContain("search_web", declarations);
        Assert.Contains("get_current_time", declarations);
        Assert.Contains("schedule_callback", declarations);
        Assert.Contains("manage_vocabulary", declarations);
        var instruction = setup["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Contains(AiCallbackService.PhoneSearchPolicy, instruction);
        Assert.Contains("today's weather", instruction);
        Assert.Contains("start a real-time voice call inside the app", instruction);
        Assert.Contains("Do not end this call", instruction, StringComparison.OrdinalIgnoreCase);

        var inApp = GeminiLiveService.SessionSetup(model, user, false, [], metadata, "Puck", null, "app", null)["setup"]!;
        Assert.Contains("search_web", inApp["tools"]!.ToJsonString());
        Assert.DoesNotContain(AiCallbackService.PhoneSearchPolicy, inApp["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.NotEqual(AiLiveResumeCache.Identity(model, user, false, metadata, "Puck", null),
            AiLiveResumeCache.Identity(model, user, false, phone, "Puck", null));

        var renewed = GeminiLiveService.SessionSetup(model, user, false, [], phone, "Puck", null, "phone", "checkpoint")["setup"]!;
        Assert.DoesNotContain("search_web", renewed["tools"]!.ToJsonString());
        Assert.Null(renewed["systemInstruction"]); // Existing provider instructions are restored.
    }

    [Fact]
    public void ClientCannotSetServerPhoneCallbackMarker()
    {
        Assert.False(AiClientMetadata.Read("""{"phoneCallback":true,"PhoneCallback":true,"deviceWebSearch":true}""").PhoneCallback);
    }

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

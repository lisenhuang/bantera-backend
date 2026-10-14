using System.Text.Json;
using BanteraApi.Chat.Ai;
using BanteraApi.Database.Entities;
using Xunit;

namespace BanteraApi.Tests;

public class AiEmojiTests
{
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    [Theory]
    [InlineData("gemini-3.8-live")]
    [InlineData("gemini-3.8-live-extended-thinking")]
    [InlineData("gemini-3.1-flash-live-preview")]
    [InlineData("gemini-2.5-flash-native-audio-latest")]
    public void EmojiDeliveryIsOptInForVoiceMessagesAndCalls(string model)
    {
        Assert.False(AiClientMetadata.Read(null).DeviceEmojis);
        Assert.True(AiClientMetadata.Read("{\"deviceEmojis\":true}").DeviceEmojis);
        foreach (var voice in new[] { true, false }) {
            Assert.DoesNotContain(AiEmojiTool.Name, JsonSerializer.Serialize(GeminiLiveService.Setup(model, "", voice)));
            var setup = JsonSerializer.SerializeToElement(GeminiLiveService.Setup(model, "", voice, deviceEmojis: true)).GetProperty("setup");
            var tool = setup.GetProperty("tools")[0].GetProperty("functionDeclarations").EnumerateArray()
                .Single(t => t.GetProperty("name").GetString() == AiEmojiTool.Name);
            if (AiLiveModelPolicy.RequiresInteractionIdle(model)) Assert.Equal("NON_BLOCKING", tool.GetProperty("behavior").GetString());
            else if (model == "gemini-3.8-live") Assert.Equal("BLOCKING", tool.GetProperty("behavior").GetString());
            else Assert.False(tool.TryGetProperty("behavior", out _));
            Assert.Contains("no internet search", setup.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task VoiceEmojiDeliveryRequiresMatchingDeviceResult()
    {
        var bridge = new AiVoiceDeviceTools();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        object? sent = null;
        var pending = bridge.InvokeAsync(Json("""{"id":"e1","name":"send_emojis","args":{"emojis":["🇮🇪","🇮🇳"]}}"""),
            frame => { sent = frame; return Task.CompletedTask; }, timeout.Token);
        Assert.Contains(AiEmojiTool.Name, JsonSerializer.Serialize(sent));
        Assert.False(bridge.Accept(Json("""[{"id":"e1","name":"manage_vocabulary","response":{"result":{"delivered":true}}}]""")));
        Assert.False(pending.IsCompleted);
        Assert.True(bridge.Accept(Json("""[{"id":"e1","name":"send_emojis","response":{"result":{"delivered":true}}}]""")));
        Assert.True(JsonSerializer.SerializeToElement(await pending).GetProperty("delivered").GetBoolean());
    }

    [Fact]
    public void CancelledLiveEmojiCallsIgnoreLateResults()
    {
        var pending = new AiPendingTools();
        var response = Json("""[{"id":"e1","name":"send_emojis","response":{"result":{"delivered":true}}}]""");
        Assert.False(pending.Accept(response));
        pending.Register(Json("""[{"id":"e1","name":"send_emojis"}]"""));
        pending.Cancel(Json("""["e1"]"""));
        Assert.True(pending.TryAccept(response, out var accepted));
        Assert.Empty(accepted.EnumerateArray());
    }

    [Fact]
    public void EnablingEmojisCannotResumeOldSessionWithoutEmojiTool()
    {
        var user = new User { Id = Guid.NewGuid() };
        var meta = new AiClientMetadata(new("UTC", 0), null, ConversationId: Guid.NewGuid().ToString());
        Assert.NotEqual(AiLiveResumeCache.Identity("gemini-3.1-flash-live-preview", user, true, meta, "Kore", null),
            AiLiveResumeCache.Identity("gemini-3.1-flash-live-preview", user, true, meta with { DeviceEmojis = true }, "Kore", null));
    }
}

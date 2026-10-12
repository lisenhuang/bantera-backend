using System.Text.Json;
using BanteraApi.Chat.Ai;
using Xunit;
namespace BanteraApi.Tests;
public class AiVocabularyTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();
    [Theory]
    [InlineData("gemini-3.8-live")]
    [InlineData("gemini-3.8-live-extended-thinking")]
    [InlineData("gemini-3.1-flash-live-preview")]
    [InlineData("gemini-2.5-flash-native-audio-latest")]
    public void VocabularyIsOptInAndCompatibleWithExistingLiveTools(string model)
    {
        Assert.False(AiClientMetadata.Read(null).DeviceVocabulary);
        Assert.True(AiClientMetadata.Read("{\"deviceVocabulary\":true}").DeviceVocabulary);
        Assert.DoesNotContain("manage_vocabulary", JsonSerializer.Serialize(GeminiLiveService.Setup(model, "", true)));
        var setup = JsonSerializer.Serialize(GeminiLiveService.Setup(model, "", true, deviceVocabulary: true));
        Assert.Contains("manage_vocabulary", setup);
        Assert.Contains("CURRENT learner", setup);
        Assert.Contains("once per day", setup);
        Assert.DoesNotContain("search_web", setup);
    }
    [Fact]
    public async Task VoiceVocabularyUsesDeviceResultAndRejectsWrongToolName()
    {
        var bridge = new AiVoiceDeviceTools();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        object? sent = null;
        var pending = bridge.InvokeAsync(Json("""{"id":"v1","name":"manage_vocabulary","args":{"action":"add","word":"broth","meaning":"thin soup"}}"""),
            value => { sent = value; return Task.CompletedTask; }, timeout.Token);
        Assert.Contains("manage_vocabulary", JsonSerializer.Serialize(sent));
        Assert.False(bridge.Accept(Json("""[{"id":"v1","name":"search_web","response":{"result":{"ok":true}}}]""")));
        Assert.False(pending.IsCompleted);
        Assert.True(bridge.Accept(Json("""[{"id":"v1","name":"manage_vocabulary","response":{"result":{"ok":true,"total":1}}}]""")));
        Assert.Contains("total", (await pending).ToString());
    }
    [Fact]
    public void LiveCallVocabularySupportsCancellationAndDoesNotAcceptUnsolicitedWrites()
    {
        var pending = new AiPendingTools();
        pending.Register(Json("""[{"id":"v1","name":"manage_vocabulary"}]"""));
        Assert.True(pending.TryAccept(Json("""[{"id":"v1","name":"manage_vocabulary","response":{"result":{"ok":true}}}]"""), out var result));
        Assert.Equal(1, result.GetArrayLength());
        Assert.False(pending.Accept(result));
        pending.Register(Json("""[{"id":"v2","name":"manage_vocabulary"}]"""));
        pending.Cancel(Json("""["v2"]"""));
        Assert.True(pending.TryAccept(Json("""[{"id":"v2","name":"manage_vocabulary","response":{"result":{"ok":true}}}]"""), out var cancelled));
        Assert.Empty(cancelled.EnumerateArray());
    }
}

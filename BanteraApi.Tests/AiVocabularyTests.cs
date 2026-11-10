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
    [Theory]
    [InlineData("gemini-3.1-flash-live-preview")]
    [InlineData("gemini-3.8-live")]
    [InlineData("gemini-3.8-live-extended-thinking")]
    public void ReadActionsAreOnlyAdvertisedToCapableDevices(string model)
    {
        var oldSetup = JsonSerializer.Serialize(GeminiLiveService.Setup(model, "", true, deviceVocabulary: true));
        var newSetup = JsonSerializer.Serialize(GeminiLiveService.Setup(model, "", true, deviceVocabulary: true, vocabularyVersion: 2));
        Assert.DoesNotContain("\"stats\"", oldSetup);
        Assert.DoesNotContain("\"quiz\"", oldSetup);
        Assert.DoesNotContain("excludeIds", oldSetup);
        Assert.Contains("\"stats\"", newSetup);
        Assert.Contains("\"latest\"", newSetup);
        Assert.Contains("\"quiz\"", newSetup);
        Assert.Contains("excludeIds", newSetup);
        Assert.Contains("ALWAYS call manage_vocabulary", newSetup);
        Assert.Contains("without revealing", newSetup);
        Assert.Contains("do not mark review", newSetup);
        Assert.DoesNotContain("manage_vocabulary", JsonSerializer.Serialize(GeminiLiveService.Setup(model, "", true, vocabularyVersion: 2)));
    }
    [Fact]
    public void ReviewV3IsNegotiatedWithoutChangingLegacyContracts()
    {
        var meta = AiClientMetadata.Read("""{"deviceVocabulary":true,"vocabularyVersion":3}""") with { ConversationId = Guid.NewGuid().ToString() };
        Assert.Equal(3, meta.VocabularyVersion);
        Assert.Equal(1, AiClientMetadata.Read("""{"vocabularyVersion":3}""").VocabularyVersion);
        var old = JsonSerializer.Serialize(AiVocabularyTool.DeclarationFor(2));
        var current = JsonSerializer.Serialize(AiVocabularyTool.DeclarationFor(3));
        Assert.Contains("\"quiz\"", old);
        Assert.DoesNotContain("review_start", old);
        Assert.Contains("review_start", current);
        Assert.Contains("review_feedback", current);
        Assert.Contains("questionId", current);
        Assert.Contains("guess_expression", current);
        Assert.DoesNotContain("\"quiz\"", current);
        var setup = JsonSerializer.Serialize(GeminiLiveService.Setup("gemini-3.1-flash-live-preview", "", true, deviceVocabulary: true, vocabularyVersion: 3));
        Assert.Contains("DEVICE REVIEW PROTOCOL V3", setup);
        Assert.Contains("dueCutoff", setup);
        Assert.Contains("FIRST use review_conversation", setup);
        Assert.Contains("Never describe an ordinary search failure as a vocabulary review failure", setup);
        Assert.Contains("VERBATIM", setup);
        Assert.DoesNotContain(AiVocabularyTool.ReadPrompt, setup);
        var user = new BanteraApi.Database.Entities.User { Id = Guid.NewGuid() };
        Assert.NotEqual(AiLiveResumeCache.Identity("gemini-3.1-flash-live-preview", user, true, meta, "Aoede", null),
            AiLiveResumeCache.Identity("gemini-3.1-flash-live-preview", user, true, meta with { VocabularyVersion = 2 }, "Aoede", null));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LegacyVocabularyPromptsRemainExactlyTheSame(int version)
    {
        var expected = AiVocabularyTool.Prompt + (version == 3 ? AiVocabularyTool.ReviewPrompt
            : version == 2 ? AiVocabularyTool.ReadPrompt : "");
        Assert.Equal(expected, AiVocabularyTool.PromptFor(version));
        Assert.DoesNotContain("PROTOCOL V4", AiVocabularyTool.PromptFor(version));
        Assert.Equal(version, AiClientMetadata.Read(JsonSerializer.Serialize(new {
            deviceVocabulary = true, vocabularyVersion = version
        })).VocabularyVersion);
    }
    [Theory]
    [InlineData(true, false)] // Voice messages and text chats share this setup.
    [InlineData(false, false)] // Foreground live calls.
    [InlineData(false, true)] // Incoming callbacks.
    public void V4ScopesReviewInstructionsToReviewWhilePreservingDeviceTools(bool message, bool callback)
    {
        var meta = AiClientMetadata.Read("""{"deviceVocabulary":true,"vocabularyVersion":4,"deviceWebSearch":true}""") with {
            ConversationId = Guid.NewGuid().ToString(), PhoneCallback = callback
        };
        var user = new BanteraApi.Database.Entities.User { Id = Guid.NewGuid() };
        var setup = GeminiLiveService.SessionSetup("gemini-3.8-live", user, message, [], meta, "Aoede", null, null, null);
        var prompt = setup["setup"]!["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Equal(4, meta.VocabularyVersion);
        Assert.Contains(AiVocabularyTool.ScopedReviewPrompt, prompt);
        Assert.DoesNotContain(AiVocabularyTool.ReviewPrompt, prompt);
        Assert.DoesNotContain("Before speaking on EVERY turn", prompt);
        Assert.Contains("review_status is a read-only inspection", prompt);
        Assert.Contains("Definitions, translations, example sentences", prompt);
        Assert.Contains("do not require review_conversation, review_status or any review authorization", prompt);
        Assert.Contains("EXACT currentId", prompt);
        Assert.Contains("VERBATIM", prompt);
        // V4 changes scoping, not tool names, payloads, or device-owned storage.
        Assert.Equal(JsonSerializer.Serialize(AiVocabularyTool.DeclarationFor(3)),
            JsonSerializer.Serialize(AiVocabularyTool.DeclarationFor(4)));
        Assert.NotEqual(AiLiveResumeCache.Identity("gemini-3.8-live", user, message, meta, "Aoede", null),
            AiLiveResumeCache.Identity("gemini-3.8-live", user, message, meta with { VocabularyVersion = 3 }, "Aoede", null));
    }
    [Theory]
    [InlineData("{\"deviceVocabulary\":true,\"vocabularyVersion\":4}", 4)]
    [InlineData("{\"deviceVocabulary\":true,\"vocabularyVersion\":100}", 4)]
    [InlineData("{\"vocabularyVersion\":4}", 1)]
    [InlineData("{\"deviceVocabulary\":false,\"vocabularyVersion\":4}", 1)]
    [InlineData("{\"deviceVocabulary\":true,\"vocabularyVersion\":\"4\"}", 1)]
    public void V4NegotiationRemainsOptInAndBounded(string json, int expected)
        => Assert.Equal(expected, AiClientMetadata.Read(json).VocabularyVersion);
    [Fact]
    public void MetadataNegotiatesReadToolsAndInvalidatesAnOlderSession()
    {
        var old = AiClientMetadata.Read("""{"deviceVocabulary":true}""");
        var current = AiClientMetadata.Read("""{"deviceVocabulary":true,"vocabularyVersion":2}""");
        Assert.Equal(1, old.VocabularyVersion);
        Assert.Equal(2, current.VocabularyVersion);
        Assert.Equal(1, AiClientMetadata.Read("""{"vocabularyVersion":2}""").VocabularyVersion);
        var user = new BanteraApi.Database.Entities.User { Id = Guid.NewGuid(), Name = "Test" };
        var conversation = Guid.NewGuid().ToString();
        old = old with { ConversationId = conversation };
        current = current with { ConversationId = conversation };
        Assert.NotEqual(AiLiveResumeCache.Identity("gemini-3.1-flash-live-preview", user, true, old, "Aoede", null),
            AiLiveResumeCache.Identity("gemini-3.1-flash-live-preview", user, true, current, "Aoede", null));
        var setup = GeminiLiveService.SessionSetup("gemini-3.1-flash-live-preview", user, true, [], current, "Aoede", null, null, null).ToJsonString();
        Assert.Contains("\"quiz\"", setup);
        Assert.Contains("ALWAYS call manage_vocabulary", setup);
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

using System.Text.Json;
using BanteraApi.Chat.Ai;
using BanteraApi.Database.Entities;
using Xunit;

namespace BanteraApi.Tests;

public class AiFormattedTextTests
{
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    [Theory]
    [InlineData("gemini-3.8-live")]
    [InlineData("gemini-3.8-live-extended-thinking")]
    [InlineData("gemini-3.1-flash-live-preview")]
    [InlineData("gemini-2.5-flash-native-audio-latest")]
    public void FormattedTextDeliveryIsOptInForVoiceMessagesAndCalls(string model)
    {
        Assert.False(AiClientMetadata.Read(null).DeviceFormattedText);
        Assert.True(AiClientMetadata.Read("{\"deviceFormattedText\":true}").DeviceFormattedText);
        foreach (var voice in new[] { true, false }) {
            Assert.DoesNotContain(AiFormattedTextTool.Name, JsonSerializer.Serialize(GeminiLiveService.Setup(model, "", voice)));
            var setup = JsonSerializer.SerializeToElement(GeminiLiveService.Setup(model, "", voice, deviceFormattedText: true)).GetProperty("setup");
            var tool = setup.GetProperty("tools")[0].GetProperty("functionDeclarations").EnumerateArray()
                .Single(t => t.GetProperty("name").GetString() == AiFormattedTextTool.Name);
            if (AiLiveModelPolicy.RequiresInteractionIdle(model)) Assert.Equal("NON_BLOCKING", tool.GetProperty("behavior").GetString());
            else if (model == "gemini-3.8-live") Assert.Equal("BLOCKING", tool.GetProperty("behavior").GetString());
            else Assert.False(tool.TryGetProperty("behavior", out _));
            Assert.Contains("same facts and meaning", setup.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task VoiceFormattedTextDeliveryRequiresMatchingDeviceResult()
    {
        var bridge = new AiVoiceDeviceTools();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        object? sent = null;
        var pending = bridge.InvokeAsync(Json("""{"id":"e1","name":"display_formatted_text","args":{"markdown":"## Example"}}"""),
            frame => { sent = frame; return Task.CompletedTask; }, timeout.Token);
        Assert.Contains(AiFormattedTextTool.Name, JsonSerializer.Serialize(sent));
        Assert.False(bridge.Accept(Json("""[{"id":"e1","name":"manage_vocabulary","response":{"result":{"delivered":true}}}]""")));
        Assert.False(pending.IsCompleted);
        Assert.True(bridge.Accept(Json("""[{"id":"e1","name":"display_formatted_text","response":{"result":{"delivered":true}}}]""")));
        Assert.True(JsonSerializer.SerializeToElement(await pending).GetProperty("delivered").GetBoolean());
    }

    [Fact]
    public void CancelledLiveFormattedTextCallsIgnoreLateResults()
    {
        var pending = new AiPendingTools();
        var response = Json("""[{"id":"e1","name":"display_formatted_text","response":{"result":{"delivered":true}}}]""");
        Assert.False(pending.Accept(response));
        pending.Register(Json("""[{"id":"e1","name":"display_formatted_text"}]"""));
        pending.Cancel(Json("""["e1"]"""));
        Assert.True(pending.TryAccept(response, out var accepted));
        Assert.Empty(accepted.EnumerateArray());
    }

    [Fact]
    public void EnablingFormattedTextCannotResumeOldSessionWithoutFormattedTextTool()
    {
        var user = new User { Id = Guid.NewGuid() };
        var meta = new AiClientMetadata(new("UTC", 0), null, ConversationId: Guid.NewGuid().ToString());
        Assert.NotEqual(AiLiveResumeCache.Identity("gemini-3.1-flash-live-preview", user, true, meta, "Kore", null),
            AiLiveResumeCache.Identity("gemini-3.1-flash-live-preview", user, true, meta with { DeviceFormattedText = true }, "Kore", null));
    }

    [AiLiveFact]
    public async Task LiveModelSendsMarkdownAndSpokenAudioSeparately()
    {
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("BANTERA_AI_LIVE_CONFIG")!));
        var key = config.RootElement.GetProperty("Gemini").GetProperty("ApiKeys")[0].GetString()!;
        const string model = "gemini-3.8-live";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        try {
            await socket.ConnectAsync(new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=" + Uri.EscapeDataString(key)), timeout.Token);
            var metadata = new AiClientMetadata(new("UTC", 0), null, true) { DeviceFormattedText = true };
            var setup = GeminiLiveService.SessionSetup(model, new User { Id = Guid.NewGuid(), Name = "Test learner", LearningLanguage = "en-NZ", NativeLanguage = "en-US" }, true, [], metadata, "Kore", null, null, null);
            await GeminiLiveService.SendAsync(socket, setup, timeout.Token);
            using var ready = await GeminiLiveService.ReceiveJsonAsync(socket, timeout.Token);
            Assert.True(ready.RootElement.TryGetProperty("setupComplete", out _));
            await GeminiLiveService.SendAsync(socket, new { clientContent = new { turns = new[] { new { role = "user", parts = new[] { new { text = "Please explain passport versus visa using Markdown: a heading, bold key terms and a two-column comparison table. Display the actual formatted answer and briefly explain it aloud." } } } }, turnComplete = true } }, timeout.Token);
            var displayed = new List<string>();
            var reply = await GeminiLiveService.ReadReplyAsync(socket, default, call => {
                if (call.GetProperty("name").GetString() != AiFormattedTextTool.Name) return Task.FromResult<object>(new { unavailable = true });
                var value = call.GetProperty("args").GetProperty("markdown").GetString()!;
                if (value.Length is 0 or > 8000) return Task.FromResult<object>(new { delivered = false });
                displayed.Add(value);
                return Task.FromResult<object>(new { delivered = true });
            }, timeout.Token);
            Assert.NotEmpty(displayed);
            var markdown = displayed.Last();
            Assert.Contains("|", markdown);
            Assert.Contains("**", markdown);
            Assert.Contains("passport", markdown, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("visa", markdown, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(reply.Pcm);
            Assert.NotEmpty(reply.OutputText);
            Assert.DoesNotContain("|", reply.OutputText);
            Console.WriteLine($"Formatted-text provider check: toolCalls={displayed.Count}; markdownCharacters={markdown.Length}; audioBytes={reply.Pcm.Length}; spokenCharacters={reply.OutputText.Length}.");
        } catch (System.Net.WebSockets.WebSocketException) {
            Assert.Fail("Live provider connection unavailable; no credentials logged.");
        }
    }
}

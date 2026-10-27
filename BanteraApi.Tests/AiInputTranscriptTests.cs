using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BanteraApi.Chat.Ai;
using Xunit;

namespace BanteraApi.Tests;

public class AiInputTranscriptTests
{
    [AiLiveFact]
    public async Task LiveVoiceMessageRefinesGrammarAndReturnsAudioInSameSession()
    {
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("BANTERA_AI_LIVE_CONFIG")!));
        var keys = config.RootElement.GetProperty("Gemini").GetProperty("ApiKeys").EnumerateArray().Select(k => k.GetString()!).ToArray();
        var path = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_AUDIO")!;
        using var file = File.OpenRead(path);
        var audio = new Microsoft.AspNetCore.Http.FormFile(file, 0, file.Length, "audio", "synthetic.wav");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var pcm = await AiAudioCodec.ReadPcmAsync(audio, timeout.Token);
        using var socket = new ClientWebSocket();
        try {
            await socket.ConnectAsync(new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=" + Uri.EscapeDataString(keys[0])), timeout.Token);
            await GeminiLiveService.SendAsync(socket, GeminiLiveService.Setup("gemini-3.8-live", "You are a friendly language partner. Reply briefly in English.", true), timeout.Token);
            using var ready = await GeminiLiveService.ReceiveJsonAsync(socket, timeout.Token);
            Assert.True(ready.RootElement.TryGetProperty("setupComplete", out _));
            var input = new AiVoiceInput();
            var upload = GeminiLiveService.UploadVoiceAsync(socket, input, timeout.Token);
            var refinements = 0;
            var reply = GeminiLiveService.ReadReplyAsync(socket, default, null, timeout.Token, uploaded: upload,
                observe: name => { if (name == "inputTranscriptRefinements") refinements++; }, refineInputTranscript: true);
            for (var offset = 0; offset < pcm.Length; offset += 3200) {
                input.Add(pcm.AsSpan(offset, Math.Min(3200, pcm.Length - offset)).ToArray());
                await Task.Delay(100, timeout.Token);
            }
            input.Commit(new(new("UTC", 0), null));
            await upload;
            var result = await reply;
            Assert.Equal(1, refinements);
            Assert.Contains("went", result.InputText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("books", result.InputText, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(result.Pcm);
            Assert.NotEmpty(result.OutputText);
            Console.WriteLine($"Same-session refinement: accepted={refinements}; input={result.InputText}; audioBytes={result.Pcm.Length}; spokenCharacters={result.OutputText.Length}.");
        } catch (WebSocketException) {
            Assert.Fail("Live provider connection unavailable; no credentials logged.");
        }
    }

    [Theory]
    [InlineData("gemini-3.8-live")]
    [InlineData("gemini-3.8-live-extended-thinking")]
    [InlineData("gemini-3.1-flash-live-preview")]
    [InlineData("gemini-2.5-flash-native-audio-preview-12-2025")]
    public void RefinementIsAvailableOnlyForVoiceMessages(string model)
    {
        foreach (var voice in new[] { false, true }) {
            var setup = JsonSerializer.SerializeToElement(GeminiLiveService.Setup(model, "", voice)).GetProperty("setup");
            var names = setup.GetProperty("tools")[0].GetProperty("functionDeclarations").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()).ToList();
            Assert.Equal(voice, names.Contains(AiInputTranscriptTool.Name));
            Assert.Equal(voice, setup.GetProperty("systemInstruction").ToString().Contains(AiInputTranscriptTool.Name));
            Assert.True(setup.TryGetProperty("inputAudioTranscription", out _));
        }
    }

    [Theory]
    [InlineData("{\"text\":\"I went home.\",\"confidence\":0.89}")]
    [InlineData("{\"text\":\"I went home.\",\"confidence\":1.1}")]
    [InlineData("{\"text\":\"I went home.\",\"confidence\":\"1\"}")]
    [InlineData("{\"text\":\" \" ,\"confidence\":1}")]
    [InlineData("{\"text\":42,\"confidence\":1}")]
    [InlineData("{}")]
    public void MalformedOrUncertainRefinementsAreRejected(string args)
    {
        using var call = JsonDocument.Parse("{\"args\":" + args + "}");
        Assert.Null(AiInputTranscriptTool.Read(call.RootElement));
    }

    [Theory]
    [InlineData("昨天我去了银行。")]
    [InlineData("昨日、銀行へ行きました。")]
    [InlineData("ذهبت إلى البنك أمس.")]
    [InlineData("I didn't pay $15 at 8:00 AM. 我昨天去了。")]
    public void UnicodeNumbersNegationAndMixedLanguagesAreNotRewrittenByServer(string text)
    {
        var call = JsonSerializer.SerializeToElement(new { args = new { text, confidence = 0.99 } });
        Assert.Equal(text, AiInputTranscriptTool.Read(call));
        Assert.Null(AiInputTranscriptTool.Read(JsonSerializer.SerializeToElement(new { args = new { text = new string('a', 12001), confidence = 1 } })));
    }

    [Fact]
    public async Task SameSessionToolReplacesOnlyFinalInputAndKeepsStreamingAudioAndCaptions()
    {
        using var socket = new Socket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var captions = new List<(string, string)>();
        var pcm = new List<byte>();
        var reply = GeminiLiveService.ReadReplyAsync(socket, default,
            _ => throw new Exception("Refinement must never reach a device or another AI"), timeout.Token,
            stream: bytes => { if (bytes is not null) pcm.AddRange(bytes); return Task.CompletedTask; },
            transcript: (role, text) => { captions.Add((role, text)); return Task.CompletedTask; }, refineInputTranscript: true);
        socket.Add("""{"serverContent":{"inputTranscription":{"text":"Yesterday I go"}}}""");
        socket.Add(Tool("Yesterday I went to the bank.", .99));
        var response = await socket.Sent.Reader.ReadAsync(timeout.Token);
        Assert.Contains("\"accepted\":true", response);
        // Tool-call completion must not be mistaken for completion of spoken audio.
        socket.Add("""{"serverContent":{"turnComplete":true}}""");
        socket.Add("""{"serverContent":{"inputTranscription":{"text":" to the bank."},"outputTranscription":{"text":"How was your visit?"},"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"AQI="}}]},"turnComplete":true}}""");
        var result = await reply;
        Assert.Equal("Yesterday I went to the bank.", result.InputText);
        Assert.Equal("How was your visit?", result.OutputText);
        Assert.Equal(new byte[] { 1, 2 }, pcm);
        Assert.Equal(new[] { ("user", "Yesterday I go"), ("user", " to the bank."), ("model", "How was your visit?") }, captions);
    }

    [Theory]
    [InlineData(false, .99)]
    [InlineData(true, .5)]
    public async Task TextCallsAndLowConfidenceKeepOriginalTranscript(bool enabled, double confidence)
    {
        using var socket = new Socket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = GeminiLiveService.ReadReplyAsync(socket, default, null, timeout.Token, refineInputTranscript: enabled);
        socket.Add(Tool("Invented correction", confidence));
        Assert.Contains("\"accepted\":false", await socket.Sent.Reader.ReadAsync(timeout.Token));
        socket.Add(Finish("Original"));
        Assert.Equal("Original", (await reply).InputText);
    }

    [Fact]
    public async Task InterruptedProvisionalCorrectionIsDiscarded()
    {
        using var socket = new Socket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = GeminiLiveService.ReadReplyAsync(socket, default, null, timeout.Token, refineInputTranscript: true);
        socket.Add(Tool("Provisional", 1));
        await socket.Sent.Reader.ReadAsync(timeout.Token);
        socket.Add("""{"serverContent":{"interrupted":true}}""");
        socket.Add(Finish("Actual new message"));
        Assert.Equal("Actual new message", (await reply).InputText);
    }

    [Fact]
    public async Task DuplicateRefinementCannotReplaceAcceptedCurrentMessage()
    {
        using var socket = new Socket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = GeminiLiveService.ReadReplyAsync(socket, default, null, timeout.Token, refineInputTranscript: true);
        socket.Add(Tool("First correction", 1));
        await socket.Sent.Reader.ReadAsync(timeout.Token);
        socket.Add(Tool("Different message", 1));
        Assert.Contains("\"accepted\":false", await socket.Sent.Reader.ReadAsync(timeout.Token));
        socket.Add(Finish("Original"));
        Assert.Equal("First correction", (await reply).InputText);
    }

    private static string Tool(string text, double confidence) => JsonSerializer.Serialize(new {
        toolCall = new { functionCalls = new[] { new { id = "refine1", name = AiInputTranscriptTool.Name, args = new { text, confidence } } } }
    });
    private static string Finish(string text) => JsonSerializer.Serialize(new {
        serverContent = new { inputTranscription = new { text }, modelTurn = new { parts = new[] { new { inlineData = new { mimeType = "audio/pcm;rate=24000", data = "AQI=" } } } }, turnComplete = true }
    });

    private sealed class Socket : WebSocket
    {
        private readonly Channel<string> incoming = Channel.CreateUnbounded<string>();
        public Channel<string> Sent { get; } = Channel.CreateUnbounded<string>();
        public void Add(string message) => incoming.Writer.TryWrite(message);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool end, CancellationToken ct) {
            Sent.Writer.TryWrite(Encoding.UTF8.GetString(bytes)); return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) {
            var bytes = Encoding.UTF8.GetBytes(await incoming.Reader.ReadAsync(ct));
            bytes.CopyTo(buffer.AsSpan());
            return new(bytes.Length, WebSocketMessageType.Text, true);
        }
    }
}

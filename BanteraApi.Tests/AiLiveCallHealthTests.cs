using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using BanteraApi.Chat.Ai;
using BanteraApi.Gemini;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class AiLiveCallHealthTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private const string Speech = """{"serverContent":{"inputTranscription":{"text":"private spoken words"}}}""";
    private const string Audio = """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"AQIDBA=="}}]}}}""";

    [Fact]
    public async Task NewCallFarewellWaitsForAudioButRenewalUsesExistingTime()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // A call with only its final 30 seconds would be due for farewell now,
        // but an initial call must not start that clock before the first audio.
        var wait = BanteraAiEndpoints.WaitForFarewellAsync(first.Task, false, 30, lifetime);
        Assert.False(wait.IsCompleted);
        first.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        var never = new TaskCompletionSource();
        await BanteraAiEndpoints.WaitForFarewellAsync(never.Task, true, 30, lifetime)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancellingBeforeGreetingDoesNotStartCountdown()
    {
        using var lifetime = new CancellationTokenSource();
        var wait = BanteraAiEndpoints.WaitForFarewellAsync(new TaskCompletionSource().Task, false, 540, lifetime);
        await lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public void NormalPauseDoesNotTimeoutButNewUnansweredSpeechDoes()
    {
        var time = new Clock(); var health = new AiLiveCallHealth(time);
        health.Observe(Json(Audio), true);
        time.Advance(180);
        Assert.Null(health.TimeoutReason());
        health.Observe(Json(Speech), false);
        time.Advance(44); Assert.Null(health.TimeoutReason());
        time.Advance(2); Assert.Equal("reply_timeout", health.TimeoutReason());
    }

    [Fact]
    public void ContinuousSpeechAndStreamingAudioRefreshDeadlineWithoutUsingMicrophoneNoise()
    {
        var time = new Clock(); var health = new AiLiveCallHealth(time);
        health.Observe(Json(Speech), false);
        for (var i = 0; i < 8; i++) {
            time.Advance(30); health.Observe(Json(Speech), false);
            Assert.Null(health.TimeoutReason());
        }
        health.Observe(Json(Audio), false);
        time.Advance(40); health.Observe(Json(Audio), false);
        time.Advance(40); Assert.Null(health.TimeoutReason());
        health.Input(32000); // PCM alone is not proof of intelligible speech.
        time.Advance(6); Assert.Equal("reply_timeout", health.TimeoutReason());
    }

    [Fact]
    public void EmptyCompletionAndKeepaliveCannotHideMissingReplyAudio()
    {
        var time = new Clock(); var health = new AiLiveCallHealth(time);
        health.Observe(Json(Speech), false);
        health.Observe(Json("""{"serverContent":{"turnComplete":true}}"""), true);
        time.Advance(46);
        health.Observe(Json("""{"usageMetadata":{"totalTokenCount":123}}"""), false);
        Assert.Equal("reply_timeout", health.TimeoutReason());
    }

    [Fact]
    public void LateInputCaptionDoesNotMakeCompletedSpokenReplyLookStalled()
    {
        var time = new Clock(); var health = new AiLiveCallHealth(time);
        health.Observe(Json(Speech), false);
        health.Observe(Json(Audio), false);
        health.Observe(Json(Speech), false);
        health.Observe(Json("""{"serverContent":{"turnComplete":true}}"""), true);
        time.Advance(90); Assert.Null(health.TimeoutReason());
    }

    [Fact]
    public void ToolsGetBoundedWaitAndReturnStartsResponseDeadline()
    {
        var time = new Clock(); var health = new AiLiveCallHealth(time);
        health.Observe(Json("""{"toolCall":{"functionCalls":[{"name":"search_web","args":{"query":"private query"}}]}}"""), false);
        time.Advance(59); Assert.Null(health.TimeoutReason());
        time.Advance(2); Assert.Equal("tool_timeout", health.TimeoutReason());
        health.ToolResponse(); Assert.Null(health.TimeoutReason());
        time.Advance(46); Assert.Equal("reply_timeout", health.TimeoutReason());
        Assert.DoesNotContain("private", JsonSerializer.Serialize(health.Snapshot()));
        Assert.DoesNotContain("search_web", JsonSerializer.Serialize(health.Snapshot()));
    }

    [Fact]
    public void InterruptionWaitsForLearnerButRetainsAlreadyRecognisedNewTurn()
    {
        var time = new Clock(); var health = new AiLiveCallHealth(time);
        health.Observe(Json(Audio), false);
        health.Observe(Json("""{"serverContent":{"interrupted":true}}"""), false);
        time.Advance(90); Assert.Null(health.TimeoutReason());
        health.Observe(Json(Speech), false);
        health.Observe(Json("""{"serverContent":{"interrupted":true}}"""), false);
        time.Advance(46); Assert.Equal("reply_timeout", health.TimeoutReason());
    }

    [Fact]
    public void SnapshotContainsOnlyNumbersFlagsAndFixedStatus()
    {
        var time = new Clock(); var health = new AiLiveCallHealth(time);
        health.Input(32000); health.Observe(Json(Speech), false);
        health.Observe(Json(Audio), true); time.Advance(10);
        var snapshot = JsonSerializer.SerializeToElement(health.Snapshot());
        Assert.Equal(32000, snapshot.GetProperty("inputBytes").GetInt64());
        Assert.Equal(4, snapshot.GetProperty("outputBytes").GetInt64());
        Assert.Equal(10000, snapshot.GetProperty("inputAgeMs").GetInt64());
        Assert.Equal(1, snapshot.GetProperty("userCaptions").GetInt64());
        Assert.DoesNotContain("private", snapshot.ToString());
        Assert.DoesNotContain("AQIDBA", snapshot.ToString());
    }

    [Fact]
    public async Task PendingReadIsCancelledAndObservedOnResponseStall()
    {
        var time = new Clock(); var health = new AiLiveCallHealth(time);
        health.Observe(Json(Speech), false);
        using var socket = new Socket();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = BanteraAiEndpoints.ReceiveCallFrameAsync(socket, health, deadline.Token, TimeSpan.FromMilliseconds(5));
        time.Advance(46);
        await Assert.ThrowsAsync<AiLiveResponseTimeoutException>(() => read);
        Assert.Equal(0, socket.ActiveReads);
        Assert.Equal(1, socket.CancelledReads);
    }

    [Theory]
    [InlineData("""{"error":{"code":429,"message":"private provider quota detail"}}""", "quota_exceeded")]
    [InlineData("""{"goAway":{"timeLeft":"0s"}}""", "session_expired")]
    public async Task RecoveryControlHasNoRawProviderMessage(string frame, string reason)
    {
        using var upstream = new Socket(); using var client = new Socket();
        upstream.Incoming.Writer.TryWrite(frame);
        var state = new BanteraAiEndpoints.CallState(AiClientMetadata.Read(null), (_, _) => Task.FromResult<object>(new { }));
        var live = new GeminiLiveService(Options.Create(new GeminiSettings()), null!, NullLogger<GeminiLiveService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await BanteraAiEndpoints.ForwardSpeakerAsync(upstream, client, state, live, deadline.Token, deadline.Token, false);
        var sent = Assert.Single(client.Sent);
        Assert.Equal("reconnect", Json(sent).GetProperty("type").GetString());
        Assert.Equal(reason, Json(sent).GetProperty("reason").GetString());
        Assert.DoesNotContain("private", sent);
        Assert.Contains(reason, JsonSerializer.Serialize(state.Health.Snapshot()));
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        public void Advance(int seconds) => ticks += seconds * 1000;
    }
    private sealed class Socket : WebSocket
    {
        public Channel<string> Incoming { get; } = Channel.CreateUnbounded<string>();
        public List<string> Sent { get; } = [];
        public int ActiveReads, CancelledReads;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool end, CancellationToken ct)
        { Sent.Add(System.Text.Encoding.UTF8.GetString(b)); return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            ActiveReads++;
            try {
                var value = await Incoming.Reader.ReadAsync(ct);
                var bytes = System.Text.Encoding.UTF8.GetBytes(value);
                bytes.CopyTo(buffer.AsSpan());
                return new(bytes.Length, WebSocketMessageType.Text, true);
            } catch (OperationCanceledException) { CancelledReads++; throw; }
            finally { ActiveReads--; }
        }
    }
}

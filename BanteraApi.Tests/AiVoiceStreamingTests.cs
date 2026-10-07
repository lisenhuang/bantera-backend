using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BanteraApi.Chat.Ai;
using Xunit;

namespace BanteraApi.Tests;

public class AiVoiceStreamingTests
{
    private static readonly AiClientMetadata Metadata = new(new("Pacific/Auckland", 780), null);
    [Fact]
    public async Task StreamsWhileRecordingButEndsActivityOnlyAfterSendAndCanReplay()
    {
        var input = new AiVoiceInput();
        input.Add(new byte[320]);
        using var socket = new Socket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var upload = GeminiLiveService.UploadVoiceAsync(socket, input, timeout.Token);
        Assert.Contains("activityStart", await socket.Sent.Reader.ReadAsync(timeout.Token));
        Assert.Contains("audio/pcm;rate=16000", await socket.Sent.Reader.ReadAsync(timeout.Token));
        Assert.False(upload.IsCompleted);
        Assert.False(input.Committed.IsCompleted);
        input.Add(new byte[640]);
        Assert.Contains("audio/pcm;rate=16000", await socket.Sent.Reader.ReadAsync(timeout.Token));
        input.Commit(Metadata);
        await upload;
        var clock = await socket.Sent.Reader.ReadAsync(timeout.Token);
        Assert.Contains("Pacific/Auckland", clock);
        Assert.Contains("realtimeInput", clock);
        Assert.DoesNotContain("clientContent", clock);
        Assert.Contains("activityEnd", await socket.Sent.Reader.ReadAsync(timeout.Token));
        var replay = new List<int>();
        await foreach (var chunk in input.ReadAsync(timeout.Token)) replay.Add(chunk.Length);
        Assert.Equal(new[] { 320, 640 }, replay);
    }
    [Fact]
    public async Task CancellationDoesNotSendActivityEndOrCommit()
    {
        var input = new AiVoiceInput();
        using var socket = new Socket();
        using var stop = new CancellationTokenSource();
        var upload = GeminiLiveService.UploadVoiceAsync(socket, input, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        Assert.False(input.Committed.IsCompleted);
        Assert.True(socket.Sent.Reader.TryRead(out var start));
        Assert.Contains("activityStart", start);
        Assert.False(socket.Sent.Reader.TryRead(out _));
    }
    [Fact]
    public void InputRejectsOversizeOddEmptyAndPostCommitAudio()
    {
        var input = new AiVoiceInput();
        Assert.Throws<InvalidDataException>(() => input.Add([]));
        Assert.Throws<InvalidDataException>(() => input.Add(new byte[3]));
        Assert.Throws<InvalidDataException>(() => input.Add(new byte[32002]));
        for (var i = 0; i < 180; i++) input.Add(new byte[32000]);
        Assert.Throws<InvalidDataException>(() => input.Add(new byte[2]));
        input.Commit(Metadata);
        Assert.Throws<InvalidDataException>(() => input.Commit(Metadata));
        Assert.Throws<InvalidDataException>(() => input.Add(new byte[320]));
    }
    [Fact]
    public async Task QuotaRetriesNextUniqueKeyButExpiryRenewsSameKey()
    {
        var attempts = new List<string>(); var cooled = new List<string>();
        var result = await GeminiLiveService.TryKeysAsync(new[] { "a", "a", "b" }, (key, _) => {
            attempts.Add(key);
            if (attempts.Count == 1) throw new AiLiveSessionExpiredException();
            if (key == "a") throw new HttpRequestException("quota", null, HttpStatusCode.TooManyRequests);
            return Task.FromResult("reply");
        }, cooled.Add, default);
        Assert.Equal("reply", result);
        Assert.Equal(new[] { "a", "a", "b" }, attempts);
        Assert.Equal(new[] { "a" }, cooled);
    }
    [Fact]
    public async Task ExpiryIsBoundedAndDoesNotCoolDownKeys()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<AiLiveSessionExpiredException>(() => GeminiLiveService.TryKeysAsync<int>(["a", "b"], (_, _) => {
            attempts++; throw new AiLiveSessionExpiredException();
        }, _ => Assert.Fail("Expiry is not quota"), default));
        Assert.Equal(3, attempts);
    }
    [Fact]
    public async Task GenericFailureAndCancellationDoNotRotateKeys()
    {
        var count = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => GeminiLiveService.TryKeysAsync<int>(["a", "b"], (_, _) => {
            count++; throw new InvalidDataException();
        }, _ => Assert.Fail("Not quota"), default));
        Assert.Equal(1, count);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GeminiLiveService.TryKeysAsync<int>(["a", "b"], (_, _) => {
            Assert.Fail("Cancelled"); return Task.FromResult(0);
        }, _ => Assert.Fail("Not quota"), cts.Token));
    }
    [Theory]
    [InlineData("{\"error\":{\"code\":429}}", true)]
    [InlineData("{\"error\":{\"status\":\"RESOURCE_EXHAUSTED\"}}", true)]
    [InlineData("{\"error\":{\"code\":500}}", false)]
    public async Task StructuredQuotaIsDistinctFromOtherFailures(string json, bool quota)
    {
        using var socket = new Socket(json);
        if (quota) {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => GeminiLiveService.ReceiveJsonAsync(socket, default));
            Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        } else await Assert.ThrowsAsync<InvalidDataException>(() => GeminiLiveService.ReceiveJsonAsync(socket, default));
    }
    [Theory]
    [InlineData("{\"goAway\":{\"timeLeft\":\"50s\"}}")]
    [InlineData("{\"error\":{\"status\":\"DEADLINE_EXCEEDED\"}}")]
    public async Task ExpiryHasItsOwnStatus(string json)
    {
        using var socket = new Socket(json);
        await Assert.ThrowsAsync<AiLiveSessionExpiredException>(() => GeminiLiveService.ReceiveJsonAsync(socket, default));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolTurnCompletionWaitsForSpokenConfirmation(bool preamble)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new Socket();
        const string audio = "{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/pcm;rate=24000\",\"data\":\"AQI=\"}}]}}}";
        if (preamble) socket.Incoming.Writer.TryWrite(audio);
        socket.Incoming.Writer.TryWrite("{\"toolCall\":{\"functionCalls\":[{\"id\":\"schedule\",\"name\":\"schedule_callback\",\"args\":{\"delaySeconds\":60}}]}}");
        socket.Incoming.Writer.TryWrite("{\"serverContent\":{\"turnComplete\":true}}");
        var invoked = 0;
        var task = GeminiLiveService.ReadReplyAsync(socket, default, _ => {
            invoked++; return Task.FromResult<object>(new {status = "scheduled"});
        }, timeout.Token);
        Assert.Contains("functionResponses", await socket.Sent.Reader.ReadAsync(timeout.Token));
        Assert.False(task.IsCompleted);
        socket.Incoming.Writer.TryWrite(audio);
        socket.Incoming.Writer.TryWrite("{\"serverContent\":{\"outputTranscription\":{\"text\":\"I will call you back.\"},\"turnComplete\":true}}");
        var reply = await task;
        Assert.Equal(1, invoked);
        Assert.Equal(preamble ? 4 : 2, reply.Pcm.Length);
        Assert.Equal("I will call you back.", reply.OutputText);
    }
    [Fact]
    public async Task StalledReplyRenewsOnceWithoutCoolingOrRotatingKey()
    {
        var attempts = new List<string>();
        var result = await GeminiLiveService.TryKeysAsync(["a", "b"], (string key, CancellationToken _) => {
            attempts.Add(key);
            if (attempts.Count == 1) throw new AiLiveResponseTimeoutException();
            return Task.FromResult("audio");
        }, _ => Assert.Fail("A stalled reply is not quota"), default);
        Assert.Equal("audio", result);
        Assert.Equal(new[] { "a", "a" }, attempts);
        var count = 0;
        await Assert.ThrowsAsync<AiLiveResponseTimeoutException>(() => GeminiLiveService.TryKeysAsync<int>(["a", "b"], (_, _) => {
            count++; throw new AiLiveResponseTimeoutException();
        }, _ => Assert.Fail("Not quota"), default));
        Assert.Equal(2, count);
    }
    [Fact]
    public async Task ReplyDeadlineStartsAtCommitAndAudioProgressExtendsIt()
    {
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var watchdog = new AiReplyWatchdog(commit.Task, default, TimeSpan.FromMilliseconds(200));
        await Task.Delay(250);
        Assert.False(watchdog.Token.IsCancellationRequested);
        commit.SetResult();
        await Task.Delay(100);
        watchdog.Progress();
        await Task.Delay(130);
        Assert.False(watchdog.Token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(2000, watchdog.Token));
        Assert.True(watchdog.TimedOut);
    }
    [Fact]
    public async Task ParentCancellationIsNotAStalledProvider()
    {
        using var parent = new CancellationTokenSource();
        await using var watchdog = new AiReplyWatchdog(Task.CompletedTask, parent.Token);
        await parent.CancelAsync();
        Assert.False(watchdog.TimedOut);
    }
    [Theory]
    [InlineData("Resource exhausted. Please try again later.", true)]
    [InlineData("Quota exceeded for concurrent sessions", true)]
    [InlineData("Rate limit exceeded", true)]
    [InlineData("RESOURCE_EXHAUSTED", true)]
    [InlineData("Deadline expired before operation could complete", false)]
    [InlineData("Internal server error", false)]
    public void CloseReasonsDistinguishQuotaFromSessionOrServiceErrors(string reason, bool quota) =>
        Assert.Equal(quota, GeminiLiveService.IsQuotaCloseReason(reason));

    [Fact]
    public async Task TranscriptFragmentsArriveBeforeCompletionAndFinalTextMatches()
    {
        using var socket = new Socket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fragments = new List<(string Role, string Text)>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = GeminiLiveService.ReadReplyAsync(socket, default, null, timeout.Token, transcript: (role, text) => {
            fragments.Add((role, text)); first.TrySetResult(); return Task.CompletedTask;
        });
        socket.Incoming.Writer.TryWrite("""{"serverContent":{"outputTranscription":{"text":"Hello "}}}""");
        await first.Task.WaitAsync(timeout.Token);
        Assert.False(task.IsCompleted);
        socket.Incoming.Writer.TryWrite("""{"serverContent":{"inputTranscription":{"text":"Hi"},"outputTranscription":{"text":"there"},"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"AQI="}}]},"turnComplete":true}}""");
        var reply = await task;
        Assert.Equal("Hello there", reply.OutputText);
        Assert.Equal(new[] { ("model", "Hello "), ("user", "Hi"), ("model", "there") }, fragments);
    }
    [Fact]
    public async Task PreCommitTranscriptsStayBufferedWithoutBlockingQuotaDetection()
    {
        using var socket = new Socket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var upload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = GeminiLiveService.ReadReplyAsync(socket, default, null, timeout.Token,
            uploaded: upload.Task, transcript: (_, _) => { Assert.Fail("Not committed"); return Task.CompletedTask; });
        socket.Incoming.Writer.TryWrite("""{"serverContent":{"inputTranscription":{"text":"private unfinished recording"}}}""");
        socket.Incoming.Writer.TryWrite("""{"error":{"code":429}}""");
        await Assert.ThrowsAsync<HttpRequestException>(() => task);
    }
    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"streamTranscripts\":false}", false)]
    [InlineData("{\"streamTranscripts\":true}", true)]
    [InlineData("{\"streamTranscripts\":\"true\"}", false)]
    public void TranscriptFramesRequireExplicitClientSupport(string start, bool expected)
    {
        using var json = JsonDocument.Parse(start);
        Assert.Equal(expected, AiVoiceMessageEndpoint.WantsTranscripts(json.RootElement));
    }

    private sealed class Socket(string? incoming = null) : WebSocket
    {
        public Channel<string> Incoming { get; } = Channel.CreateUnbounded<string>();
        public Channel<string> Sent { get; } = Channel.CreateUnbounded<string>();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType t, bool e, CancellationToken ct) {
            Sent.Writer.TryWrite(Encoding.UTF8.GetString(bytes)); return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) {
            var bytes = Encoding.UTF8.GetBytes(incoming ?? await Incoming.Reader.ReadAsync(ct)); bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }
    }
}

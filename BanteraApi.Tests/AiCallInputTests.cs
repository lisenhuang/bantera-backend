using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using BanteraApi.Chat;
using BanteraApi.Chat.Ai;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class AiCallInputTests
{
    [Fact]
    public async Task LegacyClockPacketUpdatesTimeToolsWithoutInterruptingOrDroppingAudio()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new Socket();
        using var upstream = new Socket();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options);
        var callbacks = new AiCallbackService(db, Options.Create(new ApnsSettings()));
        var metadata = new AiClientMetadata(new("UTC", 0), "voip-token", true, "alert-token", "intermediate", true, Guid.NewGuid().ToString());
        var state = new BanteraAiEndpoints.CallState(metadata,
            (current, _) => callbacks.ExecuteAsync(Guid.NewGuid(), current, "clock-test", "get_current_time", default, lifetime.Token));
        byte[] pcm = [1, 2, 3, 4];
        client.Incoming.Writer.TryWrite((WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new {
            type = "clock", clock = new { timeZone = "Pacific/Auckland", utcOffsetMinutes = 780 }
        })));
        client.Incoming.Writer.TryWrite((WebSocketMessageType.Binary, pcm));

        var forwarding = BanteraAiEndpoints.ForwardMicrophoneAsync(client, upstream, state, lifetime.Token);
        try
        {
            await upstream.FirstSend.Task.WaitAsync(lifetime.Token);
            var sent = Assert.Single(upstream.Sent);
            Assert.Equal(WebSocketMessageType.Text, sent.Type);
            using var forwarded = JsonDocument.Parse(sent.Bytes);
            var audio = forwarded.RootElement.GetProperty("realtimeInput").GetProperty("audio");
            Assert.Equal("audio/pcm;rate=16000", audio.GetProperty("mimeType").GetString());
            Assert.Equal(pcm, Convert.FromBase64String(audio.GetProperty("data").GetString()!));
            Assert.Equal(metadata with { Clock = new("Pacific/Auckland", 780) }, state.Metadata);

            var before = DateTimeOffset.UtcNow;
            var time = JsonSerializer.SerializeToElement(await state.ExecuteTool(state.Metadata, default));
            Assert.Equal("Pacific/Auckland", time.GetProperty("timeZone").GetString());
            Assert.InRange(DateTimeOffset.Parse(time.GetProperty("utc").GetString()!), before, DateTimeOffset.UtcNow);
        }
        finally
        {
            await lifetime.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forwarding);
        }
    }

    private sealed class Socket : WebSocket
    {
        public Channel<(WebSocketMessageType Type, byte[] Bytes)> Incoming { get; } = Channel.CreateUnbounded<(WebSocketMessageType, byte[])>();
        public ConcurrentQueue<(WebSocketMessageType Type, byte[] Bytes)> Sent { get; } = new();
        public TaskCompletionSource FirstSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            Sent.Enqueue((type, buffer.ToArray()));
            FirstSend.TrySetResult();
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var message = await Incoming.Reader.ReadAsync(ct);
            message.Bytes.CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(message.Bytes.Length, message.Type, true);
        }
    }
}

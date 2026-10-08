using System.Net.WebSockets;
using System.Text;
using BanteraApi.Chat.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BanteraApi.Tests;

public class AiVoiceCloseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalReplySurvivesDelayedCloseAcknowledgement(bool disconnect)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.UseWebSockets();
        var closing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Map("/voice", async context => {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var lifetime = new CancellationTokenSource();
            var receive = AiVoiceMessageEndpoint.ReceiveRecordingAsync(socket, new AiVoiceInput(), lifetime);
            await socket.SendAsync(new byte[48000], WebSocketMessageType.Binary, true, CancellationToken.None);
            await GeminiLiveService.SendAsync(socket, new { type = "complete", outputText = "Hello" }, CancellationToken.None);
            var close = AiVoiceMessageEndpoint.CloseClientAsync(socket, receive, lifetime);
            closing.TrySetResult(lifetime.IsCancellationRequested);
            await close;
            finished.TrySetResult(true);
        });
        await app.StartAsync();
        using var client = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try {
            await client.ConnectAsync(new Uri(app.Urls.Single().Replace("http://", "ws://") + "/voice"), timeout.Token);
            Assert.False(await closing.Task.WaitAsync(timeout.Token));
            if (disconnect) client.Abort();
            else {
                // The final payload must remain readable while the server waits
                // for a slow peer, not be torn down by cancelling ReceiveAsync.
                await Task.Delay(100, timeout.Token);
                var audio = await GeminiLiveService.ReceiveAsync(client, 50000, timeout.Token);
                Assert.Equal(48000, audio.Bytes.Length);
                var reply = await GeminiLiveService.ReceiveAsync(client, 1000, timeout.Token);
                Assert.Contains("\"type\":\"complete\"", Encoding.UTF8.GetString(reply.Bytes));
                var close = await client.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), timeout.Token);
                Assert.Equal(WebSocketMessageType.Close, close.MessageType);
                Assert.Equal(WebSocketCloseStatus.NormalClosure, close.CloseStatus);
                Assert.False(finished.Task.IsCompleted);
                await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "received", timeout.Token);
            }
            await finished.Task.WaitAsync(timeout.Token);
        } finally { await app.StopAsync(timeout.Token); }
    }
}

using System.Net.WebSockets;
using System.Text.Json;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Chat.Ai;

public static class AiVoiceMessageEndpoint
{
    public static async Task HandleAsync(HttpContext context, AppDbContext db, BanteraAiSettings settings,
        BanteraAiSessions sessions, GeminiLiveService live, AiCallbackService callbacks, ILoggerFactory logs)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if (!Guid.TryParse(context.User.FindFirst("sub")?.Value, out var userId)) { context.Response.StatusCode = 401; return; }
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null && u.Status == "active", context.RequestAborted);
        if (user is null || ChatLanguageResolver.Resolve(user.LearningLanguage) is null) { context.Response.StatusCode = 403; return; }
        using var lease = sessions.TryEnter(userId);
        if (lease is null) { context.Response.StatusCode = 429; return; }
        using var client = await context.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        lifetime.CancelAfter(TimeSpan.FromSeconds(20));
        Task? receive = null, generate = null;
        try {
            using var start = await GeminiLiveService.ReceiveJsonAsync(client, lifetime.Token);
            var root = start.RootElement;
            if (root.GetProperty("type").GetString() != "start" || !Guid.TryParse(root.GetProperty("requestId").GetString(), out var requestId)) throw new InvalidDataException();
            var history = AiCallPolicy.ReadHistory(root.TryGetProperty("history", out var h) ? h.GetRawText() : null);
            var snapshot = AiDeviceTools.ReadSnapshot(root.TryGetProperty("deviceData", out var d) ? d.GetRawText() : null);
            var metadata = AiClientMetadata.Read(root.TryGetProperty("metadata", out var m) ? m.GetRawText() : null);
            var input = new AiVoiceInput();
            lifetime.CancelAfter(TimeSpan.FromSeconds(190));
            await GeminiLiveService.SendAsync(client, new { type = "ready" }, lifetime.Token);
            receive = ReceiveRecordingAsync(client, input, lifetime);
            var replyTask = live.StreamReplyAsync(await settings.GetModelAsync(lifetime.Token), user, input, history, snapshot,
                metadata, await settings.GetVoiceAsync(lifetime.Token),
                async call => await callbacks.ExecuteAsync(userId, await input.Committed, requestId.ToString(),
                    call.GetProperty("name").GetString()!, call.GetProperty("args"), lifetime.Token),
                async bytes => {
                    if (bytes is null) await GeminiLiveService.SendAsync(client, new { type = "reset" }, lifetime.Token);
                    else await client.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, lifetime.Token);
                }, lifetime.Token);
            generate = replyTask;
            await await Task.WhenAny(receive, replyTask);
            var reply = await replyTask;
            await GeminiLiveService.SendAsync(client, new { type = "complete", inputText = reply.InputText, outputText = reply.OutputText }, lifetime.Token);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested) {
            logs.CreateLogger("BanteraAI").LogWarning("AI voice stream ended: {ErrorType}.", ex.GetType().Name);
            // Wait for the only output producer before sending a generic terminal error.
            await lifetime.CancelAsync();
            if (generate is not null) try { await generate; } catch { }
            if (client.State == WebSocketState.Open) try {
                using var errorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await GeminiLiveService.SendAsync(client, new { type = "error", message = "Bantera AI could not reply. Please try again shortly." }, errorTimeout.Token);
            } catch { }
        }
        finally {
            // Close output first: cancelling ReceiveAsync aborts a .NET WebSocket.
            if (client.State == WebSocketState.Open) try {
                using var end = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Voice message ended", end.Token);
            } catch { }
            await lifetime.CancelAsync();
            if (receive is not null) try { await receive; } catch { }
            if (generate is not null) try { await generate; } catch { }
        }
    }

    public static async Task ReceiveRecordingAsync(WebSocket client, AiVoiceInput input, CancellationTokenSource lifetime)
    {
        var committed = false;
        while (true) {
            var (bytes, type) = await GeminiLiveService.ReceiveAsync(client, 32000, lifetime.Token);
            if (committed) throw new InvalidDataException("Recording already sent.");
            if (type == WebSocketMessageType.Binary) { input.Add(bytes); continue; }
            if (type != WebSocketMessageType.Text) throw new InvalidDataException();
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.GetProperty("type").GetString() != "commit") throw new InvalidDataException();
            input.Commit(AiClientMetadata.Read(json.RootElement.GetProperty("metadata").GetRawText()));
            committed = true;
            lifetime.CancelAfter(TimeSpan.FromSeconds(100));
            // Keep reading to detect a cancelled/closed client during generation.
        }
    }
}

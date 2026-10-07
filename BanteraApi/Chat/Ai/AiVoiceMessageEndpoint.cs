using System.Net.WebSockets;
using System.Text.Json;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Chat.Ai;

public static class AiVoiceMessageEndpoint
{
    public static async Task HandleAsync(HttpContext context, AppDbContext db, BanteraAiSettings settings,
        BanteraAiSessions sessions, GeminiLiveService live, AiCallbackService callbacks, ILoggerFactory logs, AiChatDiagnostics diagnostics)
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
        AiVoiceInput? input = null;
        long outputBytes = 0;
        int inputChars = 0, outputChars = 0, resets = 0;
        long? firstAudioMs = null;
        Guid? correlationId = null;
        string? model = null, voice = null;
        var phase = "start";
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try {
            using var start = await GeminiLiveService.ReceiveJsonAsync(client, lifetime.Token);
            var root = start.RootElement;
            if (root.GetProperty("type").GetString() != "start" || !Guid.TryParse(root.GetProperty("requestId").GetString(), out var requestId)) throw new InvalidDataException();
            correlationId = requestId;
            var history = AiCallPolicy.ReadHistory(root.TryGetProperty("history", out var h) ? h.GetRawText() : null);
            var snapshot = AiDeviceTools.ReadSnapshot(root.TryGetProperty("deviceData", out var d) ? d.GetRawText() : null);
            var metadata = AiClientMetadata.Read(root.TryGetProperty("metadata", out var m) ? m.GetRawText() : null);
            var streamTranscripts = WantsTranscripts(root);
            input = new AiVoiceInput();
            lifetime.CancelAfter(TimeSpan.FromSeconds(190));
            await GeminiLiveService.SendAsync(client, new { type = "ready" }, lifetime.Token);
            var deviceTools = metadata.DeviceWebSearch ? new AiVoiceDeviceTools() : null;
            receive = ReceiveRecordingAsync(client, input, lifetime, deviceTools);
            model = await settings.GetModelAsync(lifetime.Token);
            voice = await settings.GetVoiceAsync(lifetime.Token);
            phase = "stream";
            var replyTask = live.StreamReplyAsync(model, user, input, history, snapshot,
                metadata, voice,
                async call => call.GetProperty("name").GetString() == AiWebSearchTool.Name
                    ? deviceTools is null ? new { unavailable = true } : await deviceTools.InvokeAsync(call,
                        frame => GeminiLiveService.SendAsync(client, frame, lifetime.Token), lifetime.Token)
                    : await callbacks.ExecuteAsync(userId, await input.Committed, requestId.ToString(),
                        call.GetProperty("name").GetString()!, call.GetProperty("args"), lifetime.Token),
                async bytes => {
                    if (bytes is null) { resets++; await GeminiLiveService.SendAsync(client, new { type = "reset" }, lifetime.Token); }
                    else { firstAudioMs ??= elapsed.ElapsedMilliseconds; outputBytes += bytes.Length; await client.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, lifetime.Token); }
                }, lifetime.Token, async (role, text) => {
                    if (role == "user") inputChars += text.Length; else outputChars += text.Length;
                    if (streamTranscripts) await GeminiLiveService.SendAsync(client, new { type = "transcript", role, text }, lifetime.Token);
                });
            generate = replyTask;
            await await Task.WhenAny(receive, replyTask);
            var reply = await replyTask;
            phase = "complete";
            await GeminiLiveService.SendAsync(client, new { type = "complete", inputText = reply.InputText, outputText = reply.OutputText }, lifetime.Token);
        }
        catch (Exception ex) {
            var reason = AiChatDiagnostics.Reason(ex, context.RequestAborted.IsCancellationRequested);
            await diagnostics.FailureAsync(userId, user.LearningLanguage, "/ws/chat/ai/voice", correlationId,
                ex, new { phase, voice, committed = input?.Committed.IsCompletedSuccessfully == true,
                    inputBytes = input?.ByteCount ?? 0, outputBytes, inputChars, outputChars, resets, firstAudioMs,
                    clientState = client.State.ToString() }, (int)elapsed.ElapsedMilliseconds, model,
                context.RequestAborted.IsCancellationRequested);
            logs.CreateLogger("BanteraAI").LogWarning(
                "AI voice stream ended: {ErrorType}; reason={Reason}; committed={Committed}; inputBytes={InputBytes}; outputBytes={OutputBytes}; elapsedMs={ElapsedMs}.",
                ex.GetType().Name, reason, input?.Committed.IsCompletedSuccessfully == true, input?.ByteCount ?? 0, outputBytes, elapsed.ElapsedMilliseconds);
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

    // Old released clients reject unknown frame types, so this is opt-in.
    public static bool WantsTranscripts(JsonElement start) =>
        start.TryGetProperty("streamTranscripts", out var value) && value.ValueKind == JsonValueKind.True;

    public static async Task ReceiveRecordingAsync(WebSocket client, AiVoiceInput input, CancellationTokenSource lifetime, AiVoiceDeviceTools? deviceTools = null)
    {
        var committed = false;
        while (true) {
            var (bytes, type) = await GeminiLiveService.ReceiveAsync(client, 32000, lifetime.Token);
            if (type == WebSocketMessageType.Binary) { if (committed) throw new InvalidDataException(); input.Add(bytes); continue; }
            if (type != WebSocketMessageType.Text) throw new InvalidDataException();
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.GetProperty("type").GetString() == "toolResponse" && committed && deviceTools is not null) {
                if (!json.RootElement.TryGetProperty("responses", out var responses) || !deviceTools.Accept(responses)) throw new InvalidDataException();
                continue;
            }
            if (committed || json.RootElement.GetProperty("type").GetString() != "commit") throw new InvalidDataException();
            input.Commit(AiClientMetadata.Read(json.RootElement.GetProperty("metadata").GetRawText()));
            committed = true;
            lifetime.CancelAfter(TimeSpan.FromSeconds(100));
            // Keep reading to detect a cancelled/closed client during generation.
        }
    }
}

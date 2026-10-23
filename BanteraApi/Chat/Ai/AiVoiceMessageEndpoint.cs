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
        using var lease = context.Request.Query["overlap"] == "1"
            ? sessions.TryEnterVoice(userId) : sessions.TryEnter(userId);
        if (lease is null) { context.Response.StatusCode = 429; return; }
        using var client = await context.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        lifetime.CancelAfter(TimeSpan.FromSeconds(20));
        Task? receive = null, generate = null;
        AiVoiceInput? input = null;
        long outputBytes = 0;
        int inputChars = 0, outputChars = 0, resets = 0, schedulingAttempts = 0;
        long? firstAudioMs = null;
        Guid? correlationId = null;
        string? model = null, voice = null;
        var phase = "start";
        var acceptNoSpeech = false;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try {
            var (startBytes, startType) = await GeminiLiveService.ReceiveAsync(client, 6 * 1024 * 1024, lifetime.Token);
            if (startType != WebSocketMessageType.Text) throw new InvalidDataException();
            using var start = JsonDocument.Parse(startBytes);
            var root = start.RootElement;
            if (root.GetProperty("type").GetString() != "start" || !Guid.TryParse(root.GetProperty("requestId").GetString(), out var requestId)) throw new InvalidDataException();
            correlationId = requestId;
            var history = AiCallPolicy.ReadHistory(root.TryGetProperty("history", out var h) ? h.GetRawText() : null);
            var snapshot = AiDeviceTools.ReadSnapshot(root.TryGetProperty("deviceData", out var d) ? d.GetRawText() : null);
            var metadata = AiClientMetadata.Read(root.TryGetProperty("metadata", out var m) ? m.GetRawText() : null);
            var streamTranscripts = WantsTranscripts(root);
            acceptNoSpeech = WantsNoSpeech(root);
            var inputMode = root.TryGetProperty("inputMode", out var mode) ? mode.GetString() : "audio";
            if (inputMode is not ("audio" or "text")) throw new InvalidDataException();
            metadata = metadata with { ReportSource = inputMode == "text" ? "text_chat" : "voice_message" };
            input = new AiVoiceInput(AiVoiceImages.Read(root), textOnly: inputMode == "text");
            lifetime.CancelAfter(TimeSpan.FromSeconds(190));
            await GeminiLiveService.SendAsync(client, new { type = "ready", acceptImages = true, acceptText = true }, lifetime.Token);
            var deviceTools = (metadata.DeviceWebSearch || metadata.DeviceVocabulary || metadata.DeviceEmojis || metadata.DeviceFormattedText) ? new AiVoiceDeviceTools() : null;
            receive = ReceiveRecordingAsync(client, input, lifetime, deviceTools);
            model = await settings.GetModelAsync(lifetime.Token);
            voice = await settings.GetVoiceAsync(lifetime.Token);
            var reasoning = await settings.GetReasoningAsync(model, lifetime.Token);
            metadata = await AiPracticeContext.LoadAsync(db, user, metadata, lifetime.Token);
            phase = "stream";
            var replyTask = live.StreamReplyAsync(model, user, input, history, snapshot,
                metadata, voice,
                async call => {
                    var name = call.GetProperty("name").GetString()!;
                    if (name is AiWebSearchTool.Name or AiVocabularyTool.Name or AiEmojiTool.Name or AiFormattedTextTool.Name)
                        return deviceTools is null ? new { unavailable = true } : await deviceTools.InvokeAsync(call,
                            frame => GeminiLiveService.SendAsync(client, frame, lifetime.Token), lifetime.Token);
                    if (name is "schedule_callback" or "schedule_reminder") schedulingAttempts++;
                    return await callbacks.ExecuteAsync(userId, (await input.Committed) with { ReportSource = metadata.ReportSource }, requestId.ToString(),
                        name, call.GetProperty("args"), lifetime.Token);
                },
                async bytes => {
                    if (bytes is null) { resets++; await GeminiLiveService.SendAsync(client, new { type = "reset" }, lifetime.Token); }
                    else { firstAudioMs ??= elapsed.ElapsedMilliseconds; outputBytes += bytes.Length; await client.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, lifetime.Token); }
                }, lifetime.Token, async (role, text) => {
                    if (role == "user") inputChars += text.Length; else outputChars += text.Length;
                    if (streamTranscripts) await GeminiLiveService.SendAsync(client, new { type = "transcript", role, text }, lifetime.Token);
                }, reasoning: reasoning);
            generate = replyTask;
            await await Task.WhenAny(receive, replyTask);
            var reply = await replyTask;
            phase = "complete";
            await GeminiLiveService.SendAsync(client, new { type = "complete", inputText = reply.InputText, outputText = reply.OutputText }, lifetime.Token);
            await diagnostics.VoiceCompletedAsync(userId, requestId, model, schedulingAttempts,
                input.ByteCount, outputBytes, (int)elapsed.ElapsedMilliseconds);
        }
        catch (AiNoSpeechException) {
            // A quiet/accidental recording is a normal outcome, not a provider
            // failure. Never emit an invented caption, reply audio, or tool call.
            logs.CreateLogger("BanteraAI").LogInformation("AI voice recording ignored: no speech; inputBytes={InputBytes}.", input?.ByteCount ?? 0);
            if (client.State == WebSocketState.Open) {
                using var end = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await GeminiLiveService.SendAsync(client, NoSpeechFrame(acceptNoSpeech), end.Token);
            }
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
            await CloseClientAsync(client, receive, lifetime);
            if (generate is not null) try { await generate; } catch { }
        }
    }

    public static async Task CloseClientAsync(WebSocket client, Task? receive, CancellationTokenSource lifetime)
    {
        // Cancelling an outstanding .NET ReceiveAsync aborts the transport. Do
        // not race that abort against the final reply and close frames: let the
        // existing receive loop consume the peer's close acknowledgement first.
        using var end = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try {
            if (client.State == WebSocketState.Open)
                await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Voice message ended", end.Token);
            if (receive is not null) await receive.WaitAsync(end.Token);
        } catch { /* Failed/disconnected peers still get bounded cleanup. */ }
        await lifetime.CancelAsync();
        if (receive is not null) try { await receive; } catch { }
    }

    // Old released clients reject unknown frame types, so this is opt-in.
    public static bool WantsTranscripts(JsonElement start) =>
        start.TryGetProperty("streamTranscripts", out var value) && value.ValueKind == JsonValueKind.True;

    public static bool WantsNoSpeech(JsonElement start) =>
        start.TryGetProperty("acceptNoSpeech", out var value) && value.ValueKind == JsonValueKind.True;

    public static object NoSpeechFrame(bool supported) => supported
        ? new { type = "no_speech", message = "No speech detected." }
        : new { type = "error", message = "No speech detected. Please record a voice message." };

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
            input.Commit(AiClientMetadata.Read(json.RootElement.GetProperty("metadata").GetRawText()),
                json.RootElement.TryGetProperty("text", out var text) ? text.GetString() : null);
            committed = true;
            lifetime.CancelAfter(TimeSpan.FromSeconds(100));
            // Keep reading to detect a cancelled/closed client during generation.
        }
    }
}

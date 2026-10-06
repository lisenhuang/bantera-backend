using System.Net.WebSockets;
using System.Security.Claims;
using BanteraApi.Database;
using BanteraApi.Gemini;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Chat.Ai;

public static class BanteraAiEndpoints
{
    public sealed record ModelRequest(string Model, string? Voice = null);
    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/bantera-ai").RequireAuthorization("Admin");
        admin.MapGet("", async (BanteraAiSettings settings, GeminiService gemini, CancellationToken ct) => {
            try {
                var catalog = await gemini.ListModelsAsync(ct);
                return Results.Ok(new { model = await settings.GetModelAsync(ct), defaultModel = settings.DefaultModel,
                    liveModels = catalog.LiveModels ?? [], voice = await settings.GetVoiceAsync(ct),
                    defaultVoice = BanteraAiSettings.DefaultVoice, voices = BanteraAiVoices.All, maxCallSeconds = settings.MaxCallSeconds });
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                return Results.Json(new { message = "Could not load available Live models. Try again shortly." }, statusCode: 503);
            }
        });
        admin.MapPut("", async (ModelRequest request, ClaimsPrincipal user, BanteraAiSettings settings, GeminiService gemini, CancellationToken ct) => {
            if (!Guid.TryParse(user.FindFirst("sub")?.Value, out var userId)) return Results.Unauthorized();
            var model = request.Model?.Trim();
            var voice = request.Voice?.Trim();
            if (voice is not null && !BanteraAiVoices.IsSupported(voice))
                return Results.BadRequest(new { message = "Choose an available Live voice." });
            if (string.IsNullOrWhiteSpace(model) || model.Length > 100) return Results.BadRequest(new { message = "Choose an available Live model." });
            try {
                var catalog = await gemini.ListModelsAsync(ct);
                if (catalog.LiveModels?.Contains(model, StringComparer.Ordinal) != true)
                    return Results.BadRequest(new { message = "Choose an available Live model." });
                await settings.SetAsync(model, voice, userId, ct);
                return Results.Ok(new { model, voice = await settings.GetVoiceAsync(ct) });
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                return Results.Json(new { message = "The model could not be confirmed or saved. Refresh and try again." }, statusCode: 503);
            }
        });
        var callbackRoutes = app.MapGroup("/api/chat/ai/callbacks").RequireAuthorization();
        callbackRoutes.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => {
            if (!Guid.TryParse(user.FindFirst("sub")?.Value, out var userId)) return Results.Unauthorized();
            var item = await db.AiCallbacks.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
            return item is null ? Results.NotFound() : Results.Ok(new { status = item.Status, dueAt = item.DueAt });
        });
        callbackRoutes.MapPost("/{id:guid}/accept", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => {
            if (!Guid.TryParse(user.FindFirst("sub")?.Value, out var userId)) return Results.Unauthorized();
            var now = DateTime.UtcNow;
            var changed = await db.AiCallbacks.Where(c => c.Id == id && c.UserId == userId && c.Status == "ringing" && c.DueAt >= now.AddSeconds(-45))
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, "answered"), ct);
            return changed == 1 ? Results.Ok() : Results.Conflict();
        });
        callbackRoutes.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => {
            if (!Guid.TryParse(user.FindFirst("sub")?.Value, out var userId)) return Results.Unauthorized();
            await db.AiCallbacks.Where(c => c.Id == id && c.UserId == userId && (c.Status == "scheduled" || c.Status == "ringing"))
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, "cancelled"), ct);
            return Results.NoContent();
        });
        app.MapPost("/api/chat/ai/reply", HandleReplyAsync).RequireAuthorization();
        app.Map("/ws/chat/ai", HandleCallAsync).RequireAuthorization();
        app.Map("/ws/chat/ai/voice", AiVoiceMessageEndpoint.HandleAsync).RequireAuthorization();
    }

    private static async Task<IResult> HandleReplyAsync(HttpContext context, AppDbContext db, BanteraAiSettings settings,
        BanteraAiSessions sessions, GeminiLiveService live, AiCallbackService callbacks, ILoggerFactory logs)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(context.User.FindFirst("sub")?.Value, out var userId)) return Results.Unauthorized();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null && u.Status == "active", context.RequestAborted);
        if (user is null || ChatLanguageResolver.Resolve(user.LearningLanguage) is null) return Results.Forbid();
        using var lease = sessions.TryEnter(userId);
        if (lease is null) return Results.Json(new { message = "Finish your current Bantera AI conversation first." }, statusCode: 429);
        if (!context.Request.HasFormContentType || context.Request.ContentLength > 10 * 1024 * 1024) return Results.BadRequest();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(100));
        try
        {
            var form = await context.Request.ReadFormAsync(new Microsoft.AspNetCore.Http.Features.FormOptions {
                MultipartBodyLengthLimit = 10 * 1024 * 1024, ValueLengthLimit = 100000
            }, timeout.Token);
            var history = AiCallPolicy.ReadHistory(form["history"]);
            var snapshot = AiDeviceTools.ReadSnapshot(form["deviceData"]);
            var metadata = AiClientMetadata.Read(form["metadata"]);
            if (!Guid.TryParse(form["requestId"], out var requestId)) return Results.BadRequest();
            var text = form["text"].ToString().Trim();
            var file = form.Files.GetFile("audio");
            if (text.Length > 4000 || (file is null && text.Length == 0) || (file is not null && (text.Length > 0 || file.Length > 10 * 1024 * 1024)))
                return Results.BadRequest();
            var pcm = file is null ? Array.Empty<byte>() : await AiAudioCodec.ReadPcmAsync(file, timeout.Token);
            var reply = await live.ReplyAsync(await settings.GetModelAsync(timeout.Token), user, pcm, history, timeout.Token, text, snapshot, metadata, call => callbacks.ExecuteAsync(userId, metadata, requestId.ToString(), call.GetProperty("name").GetString()!, call.GetProperty("args"), timeout.Token), voice: await settings.GetVoiceAsync(timeout.Token));
            // No database/R2 write: the client owns all history and audio persistence.
            return Results.Ok(new { audio = Convert.ToBase64String(AiAudioCodec.Wave(reply.Pcm)),
                inputText = text.Length > 0 ? text : reply.InputText, outputText = reply.OutputText });
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        { return Results.BadRequest(new { message = "This message could not be read. Try recording it again." }); }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            logs.CreateLogger("BanteraAI").LogWarning("AI reply failed: {ErrorType}.", ex.GetType().Name);
            return Results.Json(new { message = "Bantera AI could not reply. Please try again shortly." }, statusCode: 503);
        }
    }

    private static async Task HandleCallAsync(HttpContext context, AppDbContext db, BanteraAiSettings settings,
        BanteraAiSessions sessions, GeminiLiveService live, AiCallbackService callbacks, ILoggerFactory logs)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if (!Guid.TryParse(context.User.FindFirst("sub")?.Value, out var userId)) { context.Response.StatusCode = 401; return; }
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null && u.Status == "active", context.RequestAborted);
        if (user is null || ChatLanguageResolver.Resolve(user.LearningLanguage) is null) { context.Response.StatusCode = 403; return; }
        using var lease = sessions.TryEnter(userId);
        if (lease is null) { context.Response.StatusCode = 429; return; }
        using var client = await context.WebSockets.AcceptWebSocketAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            // Read local context before connecting. Bound the initial handshake independently.
            using var start = await GeminiLiveService.ReceiveJsonAsync(client, timeout.Token);
            if (!start.RootElement.TryGetProperty("type", out var type) || type.GetString() != "start") throw new InvalidDataException();
            var history = AiCallPolicy.ReadHistory(start.RootElement.TryGetProperty("history", out var h) ? h.GetRawText() : null);
            var metadata = AiClientMetadata.Read(start.RootElement.TryGetProperty("metadata", out var m) ? m.GetRawText() : null);
            var resuming = start.RootElement.TryGetProperty("resuming", out var resume) && resume.ValueKind == System.Text.Json.JsonValueKind.True;
            var duration = resuming && start.RootElement.TryGetProperty("remainingSeconds", out var seconds) && seconds.TryGetInt32(out var requested)
                ? Math.Clamp(requested, 1, AiCallPolicy.DurationSeconds) : AiCallPolicy.DurationSeconds;
            var callKey = Guid.NewGuid().ToString();
            var model = await settings.GetModelAsync(timeout.Token);
            var voice = await settings.GetVoiceAsync(timeout.Token);
            using var upstream = await live.ConnectAsync(model, user, false, history, timeout.Token, metadata, voice);
            timeout.CancelAfter(TimeSpan.FromSeconds(duration));
            await GeminiLiveService.SendAsync(client, new { type = "ready", maxCallSeconds = AiCallPolicy.DurationSeconds }, timeout.Token);
            await GeminiLiveService.SendAsync(upstream, new { clientContent = new {
                turns = new[] { new { role = "user", parts = new[] { new { text = resuming ? "The connection was renewed. Continue our existing audio conversation from its latest message in my learning language and accent. Do not introduce yourself or greet me again." : AiCallPolicy.Greeting } } } }, turnComplete = true
            } }, timeout.Token);
            using var conversation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var state = new CallState(metadata, (meta, call) => callbacks.ExecuteAsync(userId, meta, callKey + ":" + call.GetProperty("id").GetString(), call.GetProperty("name").GetString()!, call.GetProperty("args"), timeout.Token));
            var upload = ForwardMicrophoneAsync(client, upstream, state, timeout.Token);
            try
            {
            var download = ForwardSpeakerAsync(upstream, client, state, conversation.Token, timeout.Token);
            var farewell = Task.Delay(TimeSpan.FromSeconds(Math.Max(0, duration - 30)), timeout.Token);
            var finished = await Task.WhenAny(upload, download, farewell);
            // Stop the old generation and microphone even when the learner is mid-sentence.
            state.Ending = true;
            await conversation.CancelAsync();
            upstream.Abort();
            try { await download; } catch (Exception) when (conversation.IsCancellationRequested) { }
            if (finished == farewell && !timeout.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                await GeminiLiveService.SendAsync(client, new { type = "farewell" }, timeout.Token);
                // A separate Live turn prevents buffered old speech/turnComplete from ending the farewell early.
                var goodbye = await live.ReplyAsync(model, user, [], [], timeout.Token, AiCallPolicy.Farewell, metadata: metadata, voice: voice);
                await client.SendAsync(new ArraySegment<byte>(goodbye.Pcm), WebSocketMessageType.Binary, true, timeout.Token);
                await GeminiLiveService.SendAsync(client, new { type = "transcript", role = "model", text = goodbye.OutputText }, timeout.Token);
                await GeminiLiveService.SendAsync(client, new { type = "goodbyeComplete" }, timeout.Token);
                // Upload keeps receiving (and discarding) in-flight microphone frames. Cancelling
                // ReceiveAsync would abort the client WebSocket before the farewell can play.
                await upload;

            }
            }
            finally {
                // Deliver the expiry control and a clean close before cancelling the
                // pending client receive (which otherwise aborts its WebSocket).
                if (client.State == WebSocketState.Open) try {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Call ended", closing.Token);
                } catch { }
                await timeout.CancelAsync(); try { await upload; } catch { }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            logs.CreateLogger("BanteraAI").LogWarning("AI audio call ended: {ErrorType}.", ex.GetType().Name);
        }
        finally
        {
            if (client.State == WebSocketState.Open)
                try { using var end = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Call ended", end.Token); } catch { }
        }
    }
    private sealed class CallState(AiClientMetadata metadata, Func<AiClientMetadata, System.Text.Json.JsonElement, Task<object>> executeTool)
    {
        public volatile bool Ending;
        public readonly AiPendingTools Tools = new();
        public AiClientMetadata Metadata = metadata;
        public readonly Func<AiClientMetadata, System.Text.Json.JsonElement, Task<object>> ExecuteTool = executeTool;
        private readonly SemaphoreSlim sending = new(1, 1);
        public async Task SendAsync(WebSocket upstream, object value, CancellationToken ct) {
            await sending.WaitAsync(ct);
            try { if (!Ending) await GeminiLiveService.SendAsync(upstream, value, ct); }
            finally { sending.Release(); }
        }
    }

    private static async Task ForwardMicrophoneAsync(WebSocket client, WebSocket upstream, CallState state, CancellationToken ct)
    {
        long bytes = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var lastClock = -1d;
        while (!ct.IsCancellationRequested)
        {
            var (audio, type) = await GeminiLiveService.ReceiveAsync(client, 150000, ct);
            if (state.Ending)
            {
                if (type == WebSocketMessageType.Text)
                {
                    using var ack = System.Text.Json.JsonDocument.Parse(audio);
                    if (ack.RootElement.TryGetProperty("type", out var value) && value.GetString() == "finished") return;
                }
                continue;
            }
            if (type == WebSocketMessageType.Text)
            {
                using var response = System.Text.Json.JsonDocument.Parse(audio);
                if (response.RootElement.TryGetProperty("type", out var clockType) && clockType.GetString() == "clock")
                {
                    if (elapsed.Elapsed.TotalSeconds - lastClock < 1) continue;
                    lastClock = elapsed.Elapsed.TotalSeconds;
                    state.Metadata = state.Metadata with { Clock = AiClientMetadata.ReadClock(response.RootElement.GetProperty("clock")) };
                    await state.SendAsync(upstream, new { clientContent = new { turns = new[] { new { role = "user", parts = new[] { new { text = "[Time context only; do not respond to this note.]" + state.Metadata.TimePrompt } } } }, turnComplete = false } }, ct);
                    continue;
                }
                if (!response.RootElement.TryGetProperty("type", out var messageType) || messageType.GetString() != "toolResponse" ||
                    !response.RootElement.TryGetProperty("responses", out var responses) || !state.Tools.Accept(responses)) throw new InvalidDataException();
                try { await state.SendAsync(upstream, new { toolResponse = new { functionResponses = responses } }, ct); }
                catch (Exception) when (state.Ending && !ct.IsCancellationRequested) { }
                continue;
            }
            if (type != WebSocketMessageType.Binary || audio.Length > 32000 || audio.Length == 0 || audio.Length % 2 != 0) throw new InvalidDataException();
            bytes += audio.Length;
            if (bytes > (elapsed.Elapsed.TotalSeconds + 3) * 32000) throw new InvalidDataException();
            try { await state.SendAsync(upstream, new { realtimeInput = new { audio = new { mimeType = "audio/pcm;rate=16000", data = Convert.ToBase64String(audio) } } }, ct); }
            catch (Exception) when (state.Ending && !ct.IsCancellationRequested) { }
        }
    }
    private static async Task ForwardSpeakerAsync(WebSocket upstream, WebSocket client, CallState state, CancellationToken ct, CancellationToken clientToken)
    {
        while (!ct.IsCancellationRequested)
        {
            System.Text.Json.JsonDocument received;
            try { received = await GeminiLiveService.ReceiveJsonAsync(upstream, ct); }
            catch (AiLiveSessionExpiredException) {
                await GeminiLiveService.SendAsync(client, new { type = "reconnect", reason = "session_expired" }, clientToken);
                return;
            }
            using var message = received;
            if (message.RootElement.TryGetProperty("toolCall", out var toolCall))
            {
                var calls = toolCall.GetProperty("functionCalls");
                var local = new List<System.Text.Json.JsonElement>();
                foreach (var call in calls.EnumerateArray())
                {
                    var name = call.GetProperty("name").GetString()!;
                    if (AiCallbackService.ToolNames.Contains(name))
                    {
                        var result = await state.ExecuteTool(state.Metadata, call);
                        await state.SendAsync(upstream, new { toolResponse = new { functionResponses = new[] { new { id = call.GetProperty("id").GetString(), name, response = new { result } } } } }, ct);
                    }
                    else local.Add(call);
                }
                if (local.Count > 0) {
                    state.Tools.Register(System.Text.Json.JsonSerializer.SerializeToElement(local));
                    await GeminiLiveService.SendAsync(client, new { type = "toolCall", calls = local }, clientToken);
                }
            }
            if (!message.RootElement.TryGetProperty("serverContent", out var content)) continue;
            if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.GetBoolean())
                await GeminiLiveService.SendAsync(client, new { type = "interrupted" }, clientToken);
            if (content.TryGetProperty("inputTranscription", out _))
                await state.SendAsync(upstream, new { clientContent = new { turns = new[] { new { role = "user", parts = new[] { new { text = "[Time context for this spoken message; do not respond separately.]" + state.Metadata.TimePrompt } } } }, turnComplete = false } }, ct);
            foreach (var name in new[] { "inputTranscription", "outputTranscription" })
                if (content.TryGetProperty(name, out var transcript) && transcript.TryGetProperty("text", out var text))
                    await GeminiLiveService.SendAsync(client, new { type = "transcript", role = name == "inputTranscription" ? "user" : "model", text = text.GetString() }, clientToken);
            foreach (var audio in GeminiLiveService.AudioParts(content))
                await client.SendAsync(new ArraySegment<byte>(audio), WebSocketMessageType.Binary, true, clientToken);
            if (content.TryGetProperty("turnComplete", out var done) && done.GetBoolean())
                await GeminiLiveService.SendAsync(client, new { type = "turnComplete" }, clientToken);
        }
    }
}

using System.Net.WebSockets;
using System.Security.Claims;
using BanteraApi.Database;
using BanteraApi.Gemini;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Chat.Ai;

public static class BanteraAiEndpoints
{
    public sealed record ModelRequest(string Model, string? Voice = null, string? Reasoning = null);
    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/bantera-ai").RequireAuthorization("Admin");
        admin.MapGet("", async (BanteraAiSettings settings, GeminiService gemini, CancellationToken ct) => {
            try {
                var catalog = await gemini.ListModelsAsync(ct);
                var model = await settings.GetModelAsync(ct);
                return Results.Ok(new { model, reasoning = await settings.GetReasoningAsync(model, ct),
                    reasoningByModel = await settings.GetReasoningSelectionsAsync((catalog.LiveModels ?? []).Append(model), ct),
                    reasoningCapabilities = (catalog.LiveModels ?? []).Append(model).Distinct().ToDictionary(m => m, AiLiveReasoning.ForModel), defaultModel = settings.DefaultModel,
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
            if (request.Reasoning is not null && !AiLiveReasoning.IsSupported(model, request.Reasoning))
                return Results.BadRequest(new { message = "Choose a reasoning setting supported by the selected Live model." });
            try {
                var catalog = await gemini.ListModelsAsync(ct);
                if (catalog.LiveModels?.Contains(model, StringComparer.Ordinal) != true)
                    return Results.BadRequest(new { message = "Choose an available Live model." });
                await settings.SetAsync(model, voice, userId, ct, request.Reasoning);
                return Results.Ok(new { model, voice = await settings.GetVoiceAsync(ct), reasoning = await settings.GetReasoningAsync(model, ct) });
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                return Results.Json(new { message = "The model could not be confirmed or saved. Refresh and try again." }, statusCode: 503);
            }
        });
        app.MapPost("/api/chat/ai/diagnostics", async (AiChatDiagnostics.ClientReport report,
            ClaimsPrincipal principal, AiChatDiagnostics diagnostics) => {
            if (!Guid.TryParse(principal.FindFirst("sub")?.Value, out var userId)) return Results.Unauthorized();
            if (!AiChatDiagnostics.Valid(report)) return Results.BadRequest();
            await diagnostics.ClientAsync(userId, report);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("ai-diagnostics")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4096));
        AiConversationSummary.Map(app);
        AiReminderDelivery.Map(app);
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
        BanteraAiSessions sessions, GeminiLiveService live, AiCallbackService callbacks, ILoggerFactory logs, AiChatDiagnostics diagnostics)
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
        Guid? correlationId = null;
        string? model = null;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var inputBytes = 0;
        var acceptNoSpeech = false;
        try
        {
            var form = await context.Request.ReadFormAsync(new Microsoft.AspNetCore.Http.Features.FormOptions {
                MultipartBodyLengthLimit = 10 * 1024 * 1024, ValueLengthLimit = 100000
            }, timeout.Token);
            var history = AiCallPolicy.ReadHistory(form["history"]);
            acceptNoSpeech = string.Equals(form["acceptNoSpeech"], "true", StringComparison.Ordinal);
            var snapshot = AiDeviceTools.ReadSnapshot(form["deviceData"]);
            var metadata = AiClientMetadata.Read(form["metadata"]) with { DeviceWebSearch = false, DeviceVocabulary = false, DeviceEmojis = false, DeviceFormattedText = false };
            if (!Guid.TryParse(form["requestId"], out var requestId)) return Results.BadRequest();
            correlationId = requestId;
            var text = form["text"].ToString().Trim();
            var file = form.Files.GetFile("audio");
            if (text.Length > 4000 || (file is null && text.Length == 0) || (file is not null && (text.Length > 0 || file.Length > 10 * 1024 * 1024)))
                return Results.BadRequest();
            metadata = metadata with { ReportSource = file is null ? "text_chat" : "voice_message" };
            var pcm = file is null ? Array.Empty<byte>() : await AiAudioCodec.ReadPcmAsync(file, timeout.Token, allowEmptyRecording: acceptNoSpeech);
            inputBytes = pcm.Length;
            model = await settings.GetModelAsync(timeout.Token);
            metadata = await AiPracticeContext.LoadAsync(db, user, metadata, timeout.Token);
            var reply = await live.ReplyAsync(model, user, pcm, history, timeout.Token, text, snapshot, metadata, call => callbacks.ExecuteAsync(userId, metadata, requestId.ToString(), call.GetProperty("name").GetString()!, call.GetProperty("args"), timeout.Token), voice: await settings.GetVoiceAsync(timeout.Token), reasoning: await settings.GetReasoningAsync(model, timeout.Token));
            // The client owns history/audio; consented bug reports and schedules are stored separately.
            return Results.Ok(new { audio = Convert.ToBase64String(AiAudioCodec.Wave(reply.Pcm)),
                inputText = text.Length > 0 ? text : reply.InputText, outputText = reply.OutputText });
        }
        catch (AiNoSpeechException)
        {
            if (acceptNoSpeech) return Results.Ok(new { noSpeech = true });
            return Results.BadRequest(new { message = "No speech detected. Please record a voice message.", code = "no_speech" });
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            await diagnostics.FailureAsync(userId, user.LearningLanguage, "/api/chat/ai/reply", correlationId,
                ex, new { inputBytes }, (int)elapsed.ElapsedMilliseconds, model);
            return Results.BadRequest(new { message = "This message could not be read. Try recording it again." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            await diagnostics.FailureAsync(userId, user.LearningLanguage, "/api/chat/ai/reply", correlationId,
                ex, new { inputBytes }, (int)elapsed.ElapsedMilliseconds, model);
            logs.CreateLogger("BanteraAI").LogWarning("AI reply failed: {ErrorType}.", ex.GetType().Name);
            return Results.Json(new { message = "Bantera AI could not reply. Please try again shortly." }, statusCode: 503);
        }
    }

    private static async Task HandleCallAsync(HttpContext context, AppDbContext db, BanteraAiSettings settings,
        BanteraAiSessions sessions, GeminiLiveService live, AiCallbackService callbacks, ILoggerFactory logs, AiChatDiagnostics diagnostics)
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
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var callId = Guid.NewGuid();
        string? model = null;
        CallState? callState = null;
        var outcome = "call_ended";
        try
        {
            // Read local context before connecting. Bound the initial handshake independently.
            using var start = await GeminiLiveService.ReceiveJsonAsync(client, timeout.Token);
            if (!start.RootElement.TryGetProperty("type", out var type) || type.GetString() != "start") throw new InvalidDataException();
            var history = AiCallPolicy.ReadHistory(start.RootElement.TryGetProperty("history", out var h) ? h.GetRawText() : null);
            var metadata = AiClientMetadata.Read(start.RootElement.TryGetProperty("metadata", out var m) ? m.GetRawText() : null);
            metadata = metadata with { ReportSource = "live_call" };
            var resuming = start.RootElement.TryGetProperty("resuming", out var resume) && resume.ValueKind == System.Text.Json.JsonValueKind.True;
            if (resuming) metadata = metadata with { HasMetBanteraAi = true };
            var duration = resuming && start.RootElement.TryGetProperty("remainingSeconds", out var seconds) && seconds.TryGetInt32(out var requested)
                ? Math.Clamp(requested, 1, AiCallPolicy.DurationSeconds) : AiCallPolicy.DurationSeconds;
            string? reminder = null;
            if (start.RootElement.TryGetProperty("callbackId", out var callbackValue) && callbackValue.ValueKind == System.Text.Json.JsonValueKind.String &&
                Guid.TryParse(callbackValue.GetString(), out var callbackId))
            {
                reminder = await AiCallbackService.ReminderForCallAsync(db, userId, callbackId, timeout.Token);
                // A scheduled call has its own conversation identity, distinct
                // from a previous callback, in-app call, or voice-message turn.
                metadata = metadata with { ConversationId = callbackId.ToString(), PhoneCallback = true, DeviceWebSearch = false };
            }
            var callKey = Guid.NewGuid().ToString();
            model = await settings.GetModelAsync(timeout.Token);
            var voice = await settings.GetVoiceAsync(timeout.Token);
            var reasoning = await settings.GetReasoningAsync(model, timeout.Token);
            metadata = await AiPracticeContext.LoadAsync(db, user, metadata, timeout.Token);
            // Every new call gets its own full session and the supplied memory.
            // Only reconnecting this ongoing call may reuse a checkpoint;
            // its remaining duration above prevents restarting the countdown.
            using var upstream = await live.ConnectAsync(model, user, false, history, timeout.Token, metadata, voice, reasoning,
                forceFreshSession: !resuming);
            // New calls receive the full nine minutes from the first audio.
            // Renewals keep the existing remaining budget, including startup.
            timeout.CancelAfter(TimeSpan.FromSeconds(resuming ? duration : 35));
            await GeminiLiveService.SendAsync(client, new { type = "ready", maxCallSeconds = AiCallPolicy.DurationSeconds }, timeout.Token);
            await GeminiLiveService.SendAsync(upstream, new { clientContent = new {
                turns = new[] { new { role = "user", parts = new[] { new { text = metadata.TimePrompt + AiConversationTiming.Prompt(history, metadata.Clock) + AiCallPolicy.Opening(resuming, reminder) } } } }, turnComplete = true
            } }, timeout.Token);
            using var conversation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var state = new CallState(metadata, (meta, call) => callbacks.ExecuteAsync(userId, meta, callKey + ":" + call.GetProperty("id").GetString(), call.GetProperty("name").GetString()!, call.GetProperty("args"), timeout.Token));
            callState = state;
            var monitoring = MonitorCallAsync(diagnostics, userId, callId, model, state.Health, elapsed, conversation.Token);
            var upload = ForwardMicrophoneAsync(client, upstream, state, timeout.Token);
            var download = ForwardSpeakerAsync(upstream, client, state, live, conversation.Token, timeout.Token, AiLiveModelPolicy.RequiresInteractionIdle(model));
            var farewell = WaitForFarewellAsync(state.FirstAudio.Task, resuming, duration, timeout);
            try
            {
            var finished = await Task.WhenAny(upload, download, farewell);
            // Stop the old generation and microphone even when the learner is mid-sentence.
            // Observe genuine download failures before cancellation can hide them.
            if (finished == download) await download;
            if (finished == upload) {
                try { await upload; }
                catch (Exception ex) when (!timeout.IsCancellationRequested && ex.Data["source"] as string != "client") {
                    // A provider close can fail a microphone send just before
                    // the receiving task sends the quota/expiry recovery event.
                    try { await download.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token); }
                    catch (TimeoutException) { throw; }
                    // The speaker task either sent a recovery event or threw.
                    // Continue to the clean close when recovery was delivered.
                }
            }
            state.Ending = true;
            await conversation.CancelAsync();
            upstream.Abort();
            try { await download; } catch (Exception) when (conversation.IsCancellationRequested) { }
            if (finished == farewell && !timeout.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                await GeminiLiveService.SendAsync(client, new { type = "farewell" }, timeout.Token);
                // A separate Live turn prevents buffered old speech/turnComplete from ending the farewell early.
                var goodbye = await live.ReplyAsync(model, user, [], [], timeout.Token, AiCallPolicy.Farewell, metadata: metadata with { HasMetBanteraAi = true }, voice: voice, reasoning: reasoning);
                await client.SendAsync(new ArraySegment<byte>(goodbye.Pcm), WebSocketMessageType.Binary, true, timeout.Token);
                await GeminiLiveService.SendAsync(client, new { type = "transcript", role = "model", text = goodbye.OutputText }, timeout.Token);
                await GeminiLiveService.SendAsync(client, new { type = "goodbyeComplete" }, timeout.Token);
                // Upload keeps receiving (and discarding) in-flight microphone frames. Cancelling
                // ReceiveAsync would abort the client WebSocket before the farewell can play.
                await upload;

            }
            }
            catch {
                // Send the terminal status before finally closes the socket.
                if (client.State == WebSocketState.Open) try {
                    using var errorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await GeminiLiveService.SendAsync(client, new { type = "error", message = "Bantera AI could not continue the call. Please try again shortly." }, errorTimeout.Token);
                } catch { }
                throw;
            }
            finally {
                AiLiveResumeCache.Release(upstream);
                state.Ending = true;
                await conversation.CancelAsync();
                upstream.Abort();
                // Deliver the expiry control and a clean close before cancelling the
                // pending client receive (which otherwise aborts its WebSocket).
                if (client.State == WebSocketState.Open) try {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Call ended", closing.Token);
                } catch { }
                await timeout.CancelAsync(); try { await upload; } catch { }
                try { await download; } catch { }
                try { await farewell; } catch (OperationCanceledException) { }
                try { await monitoring; } catch (OperationCanceledException) { }
            }
        }
        catch (WebSocketException ex) when (ex.Data["source"] as string == "client" && ex.Data["closeCode"] is 1000)
        {
            outcome = "client_closed"; // Normal hang-up is not a provider failure.
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            outcome = "client_disconnected";
        }
        catch (Exception ex)
        {
            outcome = "call_failed";
            await diagnostics.FailureAsync(userId, user.LearningLanguage, "/ws/chat/ai", callId,
                ex, new { clientState = client.State.ToString(), source = ex.Data["source"] as string,
                    health = callState?.Health.Snapshot() }, (int)elapsed.ElapsedMilliseconds, model);
            logs.CreateLogger("BanteraAI").LogWarning("AI audio call ended: {ErrorType}.", ex.GetType().Name);
            if (client.State == WebSocketState.Open) try {
                using var errorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await GeminiLiveService.SendAsync(client, new { type = "error", message = "Bantera AI could not continue the call. Please try again shortly." }, errorTimeout.Token);
            } catch { }
        }
        finally
        {
            if (callState is not null)
                await diagnostics.CallAsync(userId, callId, model, outcome, callState.Health, (int)elapsed.ElapsedMilliseconds);
            if (client.State == WebSocketState.Open)
                try { using var end = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Call ended", end.Token); } catch { }
        }
    }
    internal sealed class CallState(AiClientMetadata metadata, Func<AiClientMetadata, System.Text.Json.JsonElement, Task<object>> executeTool)
    {
        public volatile bool Ending;
        public readonly TaskCompletionSource FirstAudio = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly AiLiveCallHealth Health = new();
        public readonly AiPendingTools Tools = new();
        public AiClientMetadata Metadata = metadata;
        public readonly Func<AiClientMetadata, System.Text.Json.JsonElement, Task<object>> ExecuteTool = executeTool;
        private readonly SemaphoreSlim sending = new(1, 1);
        public async Task SendAsync(WebSocket upstream, object value, CancellationToken ct) {
            await sending.WaitAsync(ct);
            try { if (!Ending) await GeminiLiveService.SendAsync(upstream, value, ct); }
            catch (Exception ex) { ex.Data["source"] = "provider_send"; throw; }
            finally { sending.Release(); }
        }
    }

    internal static async Task ForwardMicrophoneAsync(WebSocket client, WebSocket upstream, CallState state, CancellationToken ct)
    {
        long bytes = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var lastClock = -1d;
        while (!ct.IsCancellationRequested)
        {
            byte[] audio;
            WebSocketMessageType type;
            try { (audio, type) = await GeminiLiveService.ReceiveAsync(client, 150000, ct); }
            catch (Exception ex) { ex.Data["source"] = "client"; throw; }
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
                    // Older apps send clock packets when a microphone peak looks like speech.
                    // Keep them as tool metadata: any clientContent interrupts generation,
                    // even with turnComplete=false, and realtime text also counts as activity.
                    continue;
                }
                if (!response.RootElement.TryGetProperty("type", out var messageType) || messageType.GetString() != "toolResponse" ||
                    !response.RootElement.TryGetProperty("responses", out var responses) || !state.Tools.TryAccept(responses, out var accepted)) throw new InvalidDataException();
                try { if (accepted.GetArrayLength() > 0) {
                    state.Health.ToolResponse();
                    await state.SendAsync(upstream, new { toolResponse = new { functionResponses = accepted } }, ct);
                } }
                catch (Exception) when (state.Ending && !ct.IsCancellationRequested) { }
                continue;
            }
            if (type != WebSocketMessageType.Binary || audio.Length > 32000 || audio.Length == 0 || audio.Length % 2 != 0) throw new InvalidDataException();
            state.Health.Input(audio.Length);
            bytes += audio.Length;
            if (bytes > (elapsed.Elapsed.TotalSeconds + 3) * 32000) throw new InvalidDataException();
            try { await state.SendAsync(upstream, new { realtimeInput = new { audio = new { mimeType = "audio/pcm;rate=16000", data = Convert.ToBase64String(audio) } } }, ct); }
            catch (Exception) when (state.Ending && !ct.IsCancellationRequested) { }
        }
    }
    internal static async Task ForwardSpeakerAsync(WebSocket upstream, WebSocket client, CallState state, GeminiLiveService live, CancellationToken ct, CancellationToken clientToken, bool requiresInteractionIdle)
    {
        var completion = new AiLiveCompletion(requiresInteractionIdle);
        while (!ct.IsCancellationRequested)
        {
            System.Text.Json.JsonDocument received;
            try { received = await ReceiveCallFrameAsync(upstream, state.Health, ct); }
            catch (AiLiveSessionExpiredException) {
                state.Health.Recover("session_expired");
                await GeminiLiveService.SendAsync(client, new { type = "reconnect", reason = "session_expired" }, clientToken);
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests) {
                state.Health.Recover("quota_exceeded");
                live.ReportCallQuota(upstream);
                await GeminiLiveService.SendAsync(client, new { type = "reconnect", reason = "quota_exceeded" }, clientToken);
                return;
            }
            catch (AiLiveResponseTimeoutException) {
                state.Health.Recover(state.Health.TimeoutReason() ?? "response_timeout");
                await GeminiLiveService.SendAsync(client, new { type = "reconnect", reason = "response_timeout" }, clientToken);
                return;
            }
            catch (Exception ex) { ex.Data["source"] = "provider"; throw; }
            using var message = received;
            var complete = completion.Observe(message.RootElement);
            state.Health.Observe(message.RootElement, complete);
            if (message.RootElement.TryGetProperty("toolCallCancellation", out var cancellation) && cancellation.TryGetProperty("ids", out var ids)) state.Tools.Cancel(ids);
            if (message.RootElement.TryGetProperty("toolCall", out var toolCall))
            {
                var calls = toolCall.GetProperty("functionCalls");
                var local = new List<System.Text.Json.JsonElement>();
                foreach (var call in calls.EnumerateArray())
                {
                    var name = call.GetProperty("name").GetString()!;
                    if (AiCallbackService.ToolNames.Contains(name) || (state.Metadata.PhoneCallback && name == AiWebSearchTool.Name))
                    {
                        // Never relay a callback search to the device, even if the
                        // provider invents a call to this undeclared tool.
                        var result = state.Metadata.PhoneCallback && name == AiWebSearchTool.Name
                            ? (object)new { unavailable = true, reason = "in_app_call_required" }
                            : await state.ExecuteTool(state.Metadata, call);
                        state.Health.ToolResponse();
                        await state.SendAsync(upstream, new { toolResponse = new { functionResponses = new[] { new { id = call.GetProperty("id").GetString(), name, response = new { result } } } } }, ct);
                    }
                    else local.Add(call);
                }
                if (local.Count > 0) {
                    state.Tools.Register(System.Text.Json.JsonSerializer.SerializeToElement(local));
                    await GeminiLiveService.SendAsync(client, new { type = "toolCall", calls = local }, clientToken);
                }
            }
            if (!message.RootElement.TryGetProperty("serverContent", out var content)) {
                if (complete) await GeminiLiveService.SendAsync(client, new { type = "turnComplete" }, clientToken);
                continue;
            }
            if (await ForwardCallContentAsync(client, content, clientToken)) state.FirstAudio.TrySetResult();
            if (complete)
                await GeminiLiveService.SendAsync(client, new { type = "turnComplete" }, clientToken);
        }
    }
    internal static async Task WaitForFarewellAsync(Task firstAudio, bool resuming, int duration,
        CancellationTokenSource lifetime)
    {
        if (!resuming) {
            await firstAudio.WaitAsync(lifetime.Token);
            lifetime.CancelAfter(TimeSpan.FromSeconds(duration));
        }
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, duration - 30)), lifetime.Token);
    }
    private static async Task MonitorCallAsync(AiChatDiagnostics diagnostics, Guid userId, Guid callId,
        string model, AiLiveCallHealth health, System.Diagnostics.Stopwatch elapsed, CancellationToken ct)
    {
        await diagnostics.CallAsync(userId, callId, model, "call_started", health, (int)elapsed.ElapsedMilliseconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct))
            await diagnostics.CallAsync(userId, callId, model, "call_progress", health, (int)elapsed.ElapsedMilliseconds);
    }

    internal static async Task<System.Text.Json.JsonDocument> ReceiveCallFrameAsync(WebSocket upstream,
        AiLiveCallHealth health, CancellationToken ct, TimeSpan? pollInterval = null)
    {
        if (health.TimeoutReason() is not null) throw new AiLiveResponseTimeoutException();
        using var receiveDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receive = GeminiLiveService.ReceiveJsonAsync(upstream, receiveDeadline.Token);
        var delivered = false;
        try {
            while (true) {
                try {
                    var frame = await receive.WaitAsync(pollInterval ?? TimeSpan.FromSeconds(2), ct);
                    delivered = true;
                    return frame;
                }
                catch (TimeoutException) {
                    // An idle learner is allowed to be quiet. Only an outstanding
                    // greeting, spoken turn or tool result can hit this deadline.
                    if (health.TimeoutReason() is not null) throw new AiLiveResponseTimeoutException();
                }
            }
        }
        finally {
            await receiveDeadline.CancelAsync();
            // Observe cancelled reads; never leave a second receive on the socket.
            try {
                var pending = await receive;
                if (!delivered) pending.Dispose();
            } catch { }
        }
    }

    // A provider frame may contain both final response data and interruption.
    // Preserve that data in the same bubble before telling the app to finish it.
    public static async Task<bool> ForwardCallContentAsync(WebSocket client, System.Text.Json.JsonElement content, CancellationToken ct)
    {
        foreach (var name in new[] { "inputTranscription", "outputTranscription" })
            if (content.TryGetProperty(name, out var transcript) && transcript.TryGetProperty("text", out var text))
                await GeminiLiveService.SendAsync(client, new { type = "transcript", role = name == "inputTranscription" ? "user" : "model", text = text.GetString() }, ct);
        var receivedAudio = false;
        foreach (var audio in GeminiLiveService.AudioParts(content)) {
            receivedAudio |= audio.Length > 2;
            await client.SendAsync(new ArraySegment<byte>(audio), WebSocketMessageType.Binary, true, ct);
        }
        if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.GetBoolean())
            await GeminiLiveService.SendAsync(client, new { type = "interrupted" }, ct);
        return receivedAudio;
    }

}

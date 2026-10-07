using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BanteraApi.Database.Entities;
using BanteraApi.Gemini;
using Microsoft.Extensions.Options;

namespace BanteraApi.Chat.Ai;

public sealed class AiLiveSessionExpiredException : Exception
{
    public AiLiveSessionExpiredException() : base("Live session needs renewal.") { }
}

public sealed class AiLiveQuotaUnavailableException : Exception
{
    public AiLiveQuotaUnavailableException() : base("Live capacity temporarily unavailable.") { }
}

public sealed record AiVoiceReply(byte[] Pcm, string InputText, string OutputText);
public sealed record AiContextTurn(string Role, string Text);

// One active AI operation per account. Entries are removed, not retained forever.
public sealed class BanteraAiSessions
{
    private readonly ConcurrentDictionary<Guid, byte> active = new();
    public IDisposable? TryEnter(Guid user) => active.TryAdd(user, 0) ? new Lease(() => active.TryRemove(user, out _)) : null;
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? callback = release;
        public void Dispose() => Interlocked.Exchange(ref callback, null)?.Invoke();
    }
}

public sealed class GeminiLiveService(IOptions<GeminiSettings> options, GeminiKeyHealthService health,
    ILogger<GeminiLiveService> logger)
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, Action> quotaHandlers = new();
    public void ReportCallQuota(WebSocket socket)
    {
        if (quotaHandlers.TryGetValue(socket, out var report)) report();
    }

    public static object Setup(string model, string prompt, bool voiceMessage, string voice = BanteraAiVoices.Default, bool deviceWebSearch = false) => new {
        setup = new {
            model = "models/" + model,
            contextWindowCompression = new { slidingWindow = new { } },
            generationConfig = AiLiveModelPolicy.GenerationConfig(model, voice),
            systemInstruction = new { parts = new[] { new { text = prompt + (deviceWebSearch ? AiWebSearchTool.Prompt : "") + (voiceMessage ? AiCallPolicy.VoiceMessage : AiCallPolicy.TurnTaking) } } },
            tools = new[] { new { functionDeclarations = AiLiveModelPolicy.Tools(model, deviceWebSearch) } },
            inputAudioTranscription = new { }, outputAudioTranscription = new { },
            realtimeInputConfig = new { automaticActivityDetection = voiceMessage
                ? (object)new { disabled = true }
                : new {
                    disabled = false,
                    startOfSpeechSensitivity = "START_SENSITIVITY_LOW",
                    endOfSpeechSensitivity = "END_SENSITIVITY_LOW",
                    prefixPaddingMs = 300,
                    silenceDurationMs = 700
                }
            }
        }
    };

    public async Task<ClientWebSocket> ConnectAsync(string model, User user, bool voiceMessage,
        IReadOnlyList<AiContextTurn> history, CancellationToken ct, AiClientMetadata? metadata = null, string voice = BanteraAiVoices.Default)
    {
        return await WithKeysAsync(model, async (key, token) => {
            var socket = await ConnectKeyAsync(key, model, user, voiceMessage, history, token, metadata, voice);
            quotaHandlers.Add(socket, () => {
                health.CoolDown(key, model);
                logger.LogWarning("Bantera AI active call quota reached; key cooled down before reconnection.");
            });
            return socket;
        }, ct);
    }

    // Retry only explicit provider quota failures. Every eligible key gets at most one attempt.
    public static async Task<T> TryKeysAsync<T>(IEnumerable<string> keys, Func<string, CancellationToken, Task<T>> attempt,
        Action<string> coolDown, CancellationToken ct)
    {
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try {
                for (var renewals = 0; ; renewals++) {
                    try { return await attempt(key, ct); }
                    catch (AiLiveResponseTimeoutException ex) when (ex.CanReplay && renewals < 1 && !ct.IsCancellationRequested) { }
                    catch (AiLiveSessionExpiredException) when (renewals < 2 && !ct.IsCancellationRequested) {
                        // A fresh session replays the caller's context and bounded recording.
                        // Expiry is not evidence that this key has exhausted its quota.
                    }
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests && !ct.IsCancellationRequested)
            { coolDown(key); }
        }
        throw new AiLiveQuotaUnavailableException();
    }

    private async Task<T> WithKeysAsync<T>(string model, Func<string, CancellationToken, Task<T>> attempt, CancellationToken ct)
    {
        var keys = await health.EligibleKeysAsync(GeminiService.SelectKeys(options.Value.ApiKeys, false, ""), model, ct);
        return await TryKeysAsync(keys, attempt, key => {
            health.CoolDown(key, model);
            logger.LogWarning("Bantera AI quota reached for {Model}; trying another eligible key.", model);
        }, ct);
    }

    private static async Task<ClientWebSocket> ConnectKeyAsync(string key, string model, User user, bool voiceMessage,
        IReadOnlyList<AiContextTurn> history, CancellationToken ct, AiClientMetadata? metadata, string voice)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await socket.ConnectAsync(new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=" + Uri.EscapeDataString(key)), timeout.Token);
            await SendAsync(socket, Setup(model, BanteraAiIdentity.Prompt(user) + (metadata ?? new AiClientMetadata(new AiClock("UTC", 0), null)).LevelPrompt + AiCallPolicy.IntroductionPolicy(metadata, history) + (metadata ?? new AiClientMetadata(new AiClock("UTC", 0), null)).TimePrompt, voiceMessage, voice, metadata?.DeviceWebSearch == true), timeout.Token);
            using var ready = await ReceiveJsonAsync(socket, timeout.Token);
            if (!ready.RootElement.TryGetProperty("setupComplete", out _)) throw new InvalidDataException("Live setup was not accepted.");
            if (history.Count > 0)
                await SendAsync(socket, new { clientContent = new { turns = history.Select(t => new { role = t.Role, parts = new[] { new { text = t.Text } } }), turnComplete = false } }, timeout.Token);
            return socket;
        }
        catch (Exception ex)
        {
            var quota = (int)socket.HttpStatusCode == 429 || ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests };
            socket.Dispose();
            ct.ThrowIfCancellationRequested();
            if (quota) throw new HttpRequestException("Live quota unavailable.", null, System.Net.HttpStatusCode.TooManyRequests);
            if (ex is AiLiveSessionExpiredException) throw;
            // Never propagate provider exception text: it can contain the API key URL.
            throw new InvalidOperationException($"AI connection unavailable ({ex.GetType().Name}).");
        }
    }

    public async Task<AiVoiceReply> ReplyAsync(string model, User user, byte[] pcm,
        IReadOnlyList<AiContextTurn> history, CancellationToken ct, string? text = null, JsonElement snapshot = default, AiClientMetadata? metadata = null, Func<JsonElement, Task<object>>? executeTool = null, string voice = BanteraAiVoices.Default)
    {
        if (string.IsNullOrWhiteSpace(text)) {
            var input = new AiVoiceInput();
            for (var offset = 0; offset < pcm.Length; offset += 16000)
                input.Add(pcm.AsSpan(offset, Math.Min(16000, pcm.Length - offset)).ToArray());
            input.Commit(metadata ?? new(new("UTC", 0), null));
            return await StreamReplyAsync(model, user, input, history, snapshot,
                metadata ?? new(new("UTC", 0), null), voice, executeTool, _ => Task.CompletedTask, ct);
        }
        var tools = MemoizeTools(executeTool);
        return await WithKeysAsync(model, async (key, token) => {
            using var socket = await ConnectKeyAsync(key, model, user, true, history, token, metadata, voice);
            await SendAsync(socket, new { clientContent = new { turns = new[] { new { role = "user", parts = new[] { new { text } } } }, turnComplete = true } }, token);
            return await ReadReplyAsync(socket, snapshot, tools, token, requiresInteractionIdle: AiLiveModelPolicy.RequiresInteractionIdle(model));
        }, ct);
    }

    private static Func<JsonElement, Task<object>>? MemoizeTools(Func<JsonElement, Task<object>>? execute)
    {
        if (execute is null) return null;
        var results = new Dictionary<string, object>();
        return async call => {
            // Retried model turns may assign new call IDs. Do not repeat a committed callback mutation.
            var signature = call.GetProperty("name").GetString() + ":" + call.GetProperty("args").GetRawText();
            if (!results.TryGetValue(signature, out var result)) results[signature] = result = await execute(call);
            return result;
        };
    }

    public async Task<AiVoiceReply> StreamReplyAsync(string model, User user, AiVoiceInput input,
        IReadOnlyList<AiContextTurn> history, JsonElement snapshot, AiClientMetadata metadata, string voice,
        Func<JsonElement, Task<object>>? executeTool, Func<byte[]?, Task> output, CancellationToken ct,
        Func<string, string, Task>? transcript = null)
    {
        var tools = MemoizeTools(executeTool);
        var attempts = 0;
        string? capturedInput = null;
        return await WithKeysAsync(model, async (key, token) => {
            var recovering = attempts++ > 0;
            if (recovering) await output(null); // Start a distinct attempt with the same bounded recording/context.
            using var socket = await ConnectKeyAsync(key, model, user, true, history, token, metadata, voice);
            await using var watchdog = new AiReplyWatchdog(input.Committed, token);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
            var receivedAudio = false;
            var providerEvents = new Dictionary<string, int>();
            void Observe(string name) {
                providerEvents[name] = providerEvents.GetValueOrDefault(name) + 1;
                if (name == "toolResponses") watchdog.Progress();
            }
            void Annotate(Exception ex) {
                foreach (var (name, count) in providerEvents) ex.Data[name] = count;
            }
            var transcriptRecovery = recovering && !string.IsNullOrWhiteSpace(capturedInput) ? capturedInput : null;
            var attemptInput = new StringBuilder();
            // A transcription proves the audio reached Gemini. On a silent
            // attempt, commit that utterance as a text turn to the SAME Live
            // model, avoiding another proactive-audio decision on replayed PCM.
            var upload = transcriptRecovery is not null
                ? UploadTranscriptAsync(socket, input, transcriptRecovery, attempt.Token)
                : UploadVoiceAsync(socket, input, attempt.Token, requireReply: recovering);
            if (transcriptRecovery is not null && transcript is not null) await transcript("user", transcriptRecovery);
            var reply = ReadReplyAsync(socket, snapshot, tools, attempt.Token, async bytes => {
                if (bytes is null) attemptInput.Clear();
                if (bytes is { Length: > 2 }) { receivedAudio = true; watchdog.Progress(); }
                await output(bytes);
            }, upload, async (role, text) => {
                if (role == "user") { attemptInput.Append(text); capturedInput = attemptInput.ToString(); }
                if (transcript is not null) await transcript(role, text);
            }, AiLiveModelPolicy.RequiresInteractionIdle(model), Observe);
            try {
                var first = await Task.WhenAny(upload, reply);
                try { await first; }
                catch when (first == upload && !token.IsCancellationRequested) {
                    // A provider close can make SendAsync fail just before its structured
                    // quota/expiry status reaches ReceiveAsync. Prefer that precise reason.
                    try { await reply.WaitAsync(TimeSpan.FromSeconds(2), token); }
                    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests) { throw; }
                    catch (AiLiveSessionExpiredException) { throw; }
                    catch { }
                    throw;
                }
                await upload;
                var result = await reply;
                return transcriptRecovery is null ? result : result with { InputText = transcriptRecovery };
            }
            catch (OperationCanceledException) when (watchdog.TimedOut && !token.IsCancellationRequested) {
                logger.LogWarning("Bantera AI voice reply stalled after Send; canReplay={CanReplay}; not marking quota.", !receivedAudio);
                var failure = new AiLiveResponseTimeoutException(canReplay: !receivedAudio);
                Annotate(failure);
                throw failure;
            }
            catch (Exception ex) { Annotate(ex); throw; }
            finally {
                await attempt.CancelAsync();
                socket.Abort();
                try { await upload; } catch { }
                try { await reply; } catch { }
            }
        }, ct);
    }

    public static async Task UploadTranscriptAsync(WebSocket socket, AiVoiceInput input, string transcript, CancellationToken ct)
    {
        var metadata = await input.Committed.WaitAsync(ct);
        await SendAsync(socket, new { clientContent = new {
            turns = new[] { new { role = "user", parts = new[] {
                new { text = "[A completed voice message is transcribed below. Answer it aloud once; do not discuss this delivery instruction.]" + metadata.TimePrompt },
                new { text = transcript }
            } } }, turnComplete = true
        } }, ct);
    }

    public static async Task UploadVoiceAsync(WebSocket socket, AiVoiceInput input, CancellationToken ct, bool requireReply = false)
    {
        await SendAsync(socket, new { realtimeInput = new { activityStart = new { } } }, ct);
        await foreach (var chunk in input.ReadAsync(ct)) await SendAudioAsync(socket, chunk, ct);
        // Commit carries the current device timezone; trusted server time is sampled now.
        var metadata = await input.Committed.WaitAsync(ct);
        await SendAsync(socket, new { realtimeInput = new {
            text = "[Time context for this voice message; do not respond separately.]" + metadata.TimePrompt
        } }, ct);
        await SendAsync(socket, new { realtimeInput = new { activityEnd = new { } } }, ct);
        // A fresh recovery session can otherwise make the same silent decision.
        // Only after Send, explicitly commit the recorded turn. Never use this
        // in a real-time call, where silence is intentional.
        if (requireReply) await SendAsync(socket, new { clientContent = new {
            turns = new[] { new { role = "user", parts = new[] { new { text = AiCallPolicy.VoiceMessageCommit } } } },
            turnComplete = true
        } }, ct);
    }

    public static async Task<AiVoiceReply> ReadReplyAsync(WebSocket socket, JsonElement snapshot,
        Func<JsonElement, Task<object>>? executeTool, CancellationToken ct,
        Func<byte[]?, Task>? stream = null, Task? uploaded = null,
        Func<string, string, Task>? transcript = null, bool requiresInteractionIdle = false, Action<string>? observe = null)
    {
        using var audio = new MemoryStream();
        var input = new StringBuilder();
        var output = new StringBuilder();
        var awaitingToolReply = false;
        var toolRounds = 0;
        var inputSent = 0;
        var outputSent = 0;
        var completion = new AiLiveCompletion(requiresInteractionIdle);
        while (true)
        {
            using var json = await ReceiveJsonAsync(socket, ct);
            if (json.RootElement.TryGetProperty("toolCall", out var toolCall))
            {
                if (++toolRounds > 16) throw new InvalidDataException("Too many Live tool rounds.");
                awaitingToolReply = true;
                if (uploaded is not null) await uploaded.WaitAsync(ct);
                var responses = new List<object>();
                foreach (var call in toolCall.GetProperty("functionCalls").EnumerateArray())
                {
                    var name = call.GetProperty("name").GetString()!;
                    if ((AiCallbackService.ToolNames.Contains(name) || name == AiWebSearchTool.Name) && executeTool is not null)
                        responses.Add(new { id = call.GetProperty("id").GetString(), name, response = new { result = await executeTool(call) } });
                    else responses.Add(AiDeviceTools.SnapshotResponse(call, snapshot));
                }
                await SendAsync(socket, new { toolResponse = new { functionResponses = responses } }, ct);
                observe?.Invoke("toolResponses");
            }
            // An explicit recovery commit can cancel a provisional provider
            // turn. Its completion is not the answer to the committed message.
            if (json.RootElement.TryGetProperty("serverContent", out var interruptedContent) &&
                interruptedContent.TryGetProperty("interrupted", out var interrupted) && interrupted.ValueKind == JsonValueKind.True) {
                observe?.Invoke("providerInterruptions");
                AppendTranscript(interruptedContent, "inputTranscription", input);
                audio.SetLength(0);
                output.Clear();
                inputSent = outputSent = 0;
                completion = new AiLiveCompletion(requiresInteractionIdle);
                if (stream is not null) await stream(null);
                continue;
            }
            var complete = completion.Observe(json.RootElement);
            if (!json.RootElement.TryGetProperty("serverContent", out var content)) {
                if (complete && audio.Length > 0) break;
                continue;
            }
            AppendTranscript(content, "inputTranscription", input);
            AppendTranscript(content, "outputTranscription", output);
            // Continue receiving provider frames during recording (including
            // quota errors); buffer captions until the user has pressed Send.
            if (transcript is not null && (uploaded is null || uploaded.IsCompletedSuccessfully)) {
                if (input.Length > inputSent) {
                    await transcript("user", input.ToString(inputSent, input.Length - inputSent));
                    inputSent = input.Length;
                }
                if (output.Length > outputSent) {
                    await transcript("model", output.ToString(outputSent, output.Length - outputSent));
                    outputSent = output.Length;
                }
            }
            if (content.TryGetProperty("modelTurn", out var modelTurn) && modelTurn.TryGetProperty("parts", out var parts))
                foreach (var part in parts.EnumerateArray())
                    if (part.TryGetProperty("inlineData", out var inline))
                        observe?.Invoke(inline.TryGetProperty("mimeType", out var mime) && mime.GetString()?.StartsWith("audio/pcm", StringComparison.Ordinal) == true ? "pcmParts" : "otherInlineParts");
            foreach (var chunk in AudioParts(content))
            {
                if (audio.Length + chunk.Length > 24000 * 2 * 90) throw new InvalidDataException("AI reply too long.");
                if (uploaded is not null) await uploaded.WaitAsync(ct);
                awaitingToolReply = false;
                audio.Write(chunk);
                if (stream is not null) await stream(chunk);
            }
            if (complete) {
                // Live can finish the tool-call turn before generating the spoken
                // confirmation. Keep this socket open for the tool-result turn.
                if (awaitingToolReply) { awaitingToolReply = false; continue; }
                // A completion can close an interrupted/provisional turn before
                // the replacement AUDIO turn arrives. Transcription alone is
                // not a voice reply. The post-Send watchdog bounds this wait.
                if (audio.Length == 0) { observe?.Invoke("audioFreeCompletions"); continue; }
                break;
            }
        }
        if (audio.Length == 0) throw new InvalidDataException("AI returned no audio.");
        return new(audio.ToArray(), input.ToString(), output.ToString());
    }

    public static void AppendTranscript(JsonElement content, string name, StringBuilder text)
    {
        if (content.TryGetProperty(name, out var transcript) && transcript.TryGetProperty("text", out var value) && text.Length < 12000)
            text.Append(value.GetString());
    }

    public static IEnumerable<byte[]> AudioParts(JsonElement content)
    {
        if (!content.TryGetProperty("modelTurn", out var turn) || !turn.TryGetProperty("parts", out var parts)) yield break;
        foreach (var part in parts.EnumerateArray())
            if (part.TryGetProperty("inlineData", out var data) && data.TryGetProperty("mimeType", out var mime) &&
                mime.GetString()?.StartsWith("audio/pcm", StringComparison.Ordinal) == true && data.TryGetProperty("data", out var bytes))
                yield return Convert.FromBase64String(bytes.GetString()!);
    }

    public static Task SendAudioAsync(WebSocket socket, ReadOnlyMemory<byte> pcm, CancellationToken ct) =>
        SendAsync(socket, new { realtimeInput = new { audio = new { mimeType = "audio/pcm;rate=16000", data = Convert.ToBase64String(pcm.Span) } } }, ct);
    public static Task SendAsync(WebSocket socket, object value, CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(value)), WebSocketMessageType.Text, true, ct);
    public static async Task<JsonDocument> ReceiveJsonAsync(WebSocket socket, CancellationToken ct)
    {
        var (bytes, type) = await ReceiveAsync(socket, 2 * 1024 * 1024, ct);
        // Gemini can transport its UTF-8 JSON in binary WebSocket frames.
        if (type is not (WebSocketMessageType.Text or WebSocketMessageType.Binary)) throw new InvalidDataException("Expected Live JSON.");
        var json = JsonDocument.Parse(bytes);
        if (json.RootElement.TryGetProperty("goAway", out _)) {
            json.Dispose();
            throw new AiLiveSessionExpiredException();
        }
        if (json.RootElement.TryGetProperty("error", out var error)) {
            var quota = (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var status) && status == 429)
                || (error.TryGetProperty("status", out var label) && label.ValueKind == JsonValueKind.String && label.GetString() == "RESOURCE_EXHAUSTED")
                || (error.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String && IsQuotaCloseReason(detail.GetString() ?? ""));
            var expired = error.TryGetProperty("status", out var expiry) && expiry.ValueKind == JsonValueKind.String && expiry.GetString() == "DEADLINE_EXCEEDED";
            var providerCode = error.TryGetProperty("code", out var rawCode) && rawCode.ValueKind == JsonValueKind.Number && rawCode.TryGetInt32(out var number) ? number : (int?)null;
            json.Dispose();
            if (expired) throw new AiLiveSessionExpiredException();
            if (quota) throw new HttpRequestException("Live quota unavailable.", null, System.Net.HttpStatusCode.TooManyRequests);
            var failure = new InvalidDataException("Live request failed.");
            if (providerCode is not null) failure.Data["providerCode"] = providerCode.Value;
            throw failure;
        }
        return json;
    }
    public static bool IsQuotaCloseReason(string reason) =>
        reason.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("resource exhausted", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("too many requests", StringComparison.OrdinalIgnoreCase);

    public static async Task<(byte[] Bytes, WebSocketMessageType Type)> ReceiveAsync(WebSocket socket, int maxBytes, CancellationToken ct)
    {
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (part.MessageType == WebSocketMessageType.Close) {
                var reason = part.CloseStatusDescription ?? "";
                if (IsQuotaCloseReason(reason))
                    throw new HttpRequestException("Live quota unavailable.", null, System.Net.HttpStatusCode.TooManyRequests);
                if (reason.Contains("Deadline expired before operation could complete", StringComparison.OrdinalIgnoreCase))
                    throw new AiLiveSessionExpiredException();
                var failure = new WebSocketException("Session closed.");
                if (part.CloseStatus is not null) failure.Data["closeCode"] = (int)part.CloseStatus.Value;
                throw failure;
            }
            if (data.Length + part.Count > maxBytes) throw new InvalidDataException("Frame too large.");
            data.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return (data.ToArray(), part.MessageType);
    }
}

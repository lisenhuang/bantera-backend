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
    public static object Setup(string model, string prompt, bool voiceMessage, string voice = BanteraAiVoices.Default) => new {
        setup = new {
            model = "models/" + model,
            contextWindowCompression = new { slidingWindow = new { } },
            generationConfig = new { responseModalities = new[] { "AUDIO" }, maxOutputTokens = 2048,
                speechConfig = new { voiceConfig = new { prebuiltVoiceConfig = new { voiceName = voice } } } },
            systemInstruction = new { parts = new[] { new { text = prompt } } },
            tools = new[] { new { functionDeclarations = AiDeviceTools.Declarations.Concat(AiCallbackService.Declarations).ToArray() } },
            inputAudioTranscription = new { }, outputAudioTranscription = new { },
            realtimeInputConfig = new { automaticActivityDetection = new { disabled = voiceMessage } }
        }
    };

    public async Task<ClientWebSocket> ConnectAsync(string model, User user, bool voiceMessage,
        IReadOnlyList<AiContextTurn> history, CancellationToken ct, AiClientMetadata? metadata = null, string voice = BanteraAiVoices.Default)
    {
        return await WithKeysAsync(model, (key, token) => ConnectKeyAsync(key, model, user, voiceMessage, history, token, metadata, voice), ct);
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
                    catch (AiLiveSessionExpiredException) when (renewals < 2 && !ct.IsCancellationRequested) {
                        // A fresh session replays the caller's context and bounded recording.
                        // Expiry is not evidence that this key has exhausted its quota.
                    }
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests && !ct.IsCancellationRequested)
            { coolDown(key); }
        }
        throw new InvalidOperationException("AI connection unavailable.");
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
            await SendAsync(socket, Setup(model, BanteraAiIdentity.Prompt(user) + (metadata ?? new AiClientMetadata(new AiClock("UTC", 0), null)).TimePrompt, voiceMessage, voice), timeout.Token);
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
            return await ReadReplyAsync(socket, snapshot, tools, token);
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
        Func<JsonElement, Task<object>>? executeTool, Func<byte[]?, Task> output, CancellationToken ct)
    {
        var tools = MemoizeTools(executeTool);
        var attempts = 0;
        return await WithKeysAsync(model, async (key, token) => {
            if (attempts++ > 0) await output(null); // Discard a partial response before replaying on another key.
            using var socket = await ConnectKeyAsync(key, model, user, true, history, token, metadata, voice);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            var upload = UploadVoiceAsync(socket, input, attempt.Token);
            var reply = ReadReplyAsync(socket, snapshot, tools, attempt.Token, output, upload);
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
                return await reply;
            }
            finally {
                await attempt.CancelAsync();
                socket.Abort();
                try { await upload; } catch { }
                try { await reply; } catch { }
            }
        }, ct);
    }

    public static async Task UploadVoiceAsync(WebSocket socket, AiVoiceInput input, CancellationToken ct)
    {
        await SendAsync(socket, new { realtimeInput = new { activityStart = new { } } }, ct);
        await foreach (var chunk in input.ReadAsync(ct)) await SendAudioAsync(socket, chunk, ct);
        // Commit carries the current device timezone; trusted server time is sampled now.
        var metadata = await input.Committed.WaitAsync(ct);
        await SendAsync(socket, new { clientContent = new { turns = new[] { new { role = "user", parts = new[] {
            new { text = "[Time context for this voice message; do not respond separately.]" + metadata.TimePrompt }
        } } }, turnComplete = false } }, ct);
        await SendAsync(socket, new { realtimeInput = new { activityEnd = new { } } }, ct);
    }

    private static async Task<AiVoiceReply> ReadReplyAsync(WebSocket socket, JsonElement snapshot,
        Func<JsonElement, Task<object>>? executeTool, CancellationToken ct,
        Func<byte[]?, Task>? stream = null, Task? uploaded = null)
    {
        using var audio = new MemoryStream();
        var input = new StringBuilder();
        var output = new StringBuilder();
        while (true)
        {
            using var json = await ReceiveJsonAsync(socket, ct);
            if (json.RootElement.TryGetProperty("toolCall", out var toolCall))
            {
                if (uploaded is not null) await uploaded.WaitAsync(ct);
                var responses = new List<object>();
                foreach (var call in toolCall.GetProperty("functionCalls").EnumerateArray())
                {
                    var name = call.GetProperty("name").GetString()!;
                    if (AiCallbackService.ToolNames.Contains(name) && executeTool is not null)
                        responses.Add(new { id = call.GetProperty("id").GetString(), name, response = new { result = await executeTool(call) } });
                    else responses.Add(AiDeviceTools.SnapshotResponse(call, snapshot));
                }
                await SendAsync(socket, new { toolResponse = new { functionResponses = responses } }, ct);
            }
            if (!json.RootElement.TryGetProperty("serverContent", out var content)) continue;
            AppendTranscript(content, "inputTranscription", input);
            AppendTranscript(content, "outputTranscription", output);
            foreach (var chunk in AudioParts(content))
            {
                if (audio.Length + chunk.Length > 24000 * 2 * 90) throw new InvalidDataException("AI reply too long.");
                if (uploaded is not null) await uploaded.WaitAsync(ct);
                audio.Write(chunk);
                if (stream is not null) await stream(chunk);
            }
            if (content.TryGetProperty("turnComplete", out var done) && done.GetBoolean()) break;
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
                || (error.TryGetProperty("status", out var label) && label.ValueKind == JsonValueKind.String && label.GetString() == "RESOURCE_EXHAUSTED");
            var expired = error.TryGetProperty("status", out var expiry) && expiry.ValueKind == JsonValueKind.String && expiry.GetString() == "DEADLINE_EXCEEDED";
            json.Dispose();
            if (expired) throw new AiLiveSessionExpiredException();
            if (quota) throw new HttpRequestException("Live quota unavailable.", null, System.Net.HttpStatusCode.TooManyRequests);
            throw new InvalidDataException("Live request failed.");
        }
        return json;
    }
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
                if (reason.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) || reason.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException("Live quota unavailable.", null, System.Net.HttpStatusCode.TooManyRequests);
                if (reason.Contains("Deadline expired before operation could complete", StringComparison.OrdinalIgnoreCase))
                    throw new AiLiveSessionExpiredException();
                throw new WebSocketException("Session closed.");
            }
            if (data.Length + part.Count > maxBytes) throw new InvalidDataException("Frame too large.");
            data.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return (data.ToArray(), part.MessageType);
    }
}

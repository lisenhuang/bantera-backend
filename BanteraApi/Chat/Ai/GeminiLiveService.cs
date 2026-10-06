using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BanteraApi.Database.Entities;
using BanteraApi.Gemini;
using Microsoft.Extensions.Options;

namespace BanteraApi.Chat.Ai;

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
    public static object Setup(string model, string prompt, bool voiceMessage) => new {
        setup = new {
            model = "models/" + model,
            contextWindowCompression = new { slidingWindow = new { } },
            generationConfig = new { responseModalities = new[] { "AUDIO" }, maxOutputTokens = 2048 },
            systemInstruction = new { parts = new[] { new { text = prompt } } },
            tools = new[] { new { functionDeclarations = AiDeviceTools.Declarations.Concat(AiCallbackService.Declarations).ToArray() } },
            inputAudioTranscription = new { }, outputAudioTranscription = new { },
            realtimeInputConfig = new { automaticActivityDetection = new { disabled = voiceMessage } }
        }
    };

    public async Task<ClientWebSocket> ConnectAsync(string model, User user, bool voiceMessage,
        IReadOnlyList<AiContextTurn> history, CancellationToken ct, AiClientMetadata? metadata = null)
    {
        var keys = await health.EligibleKeysAsync(GeminiService.SelectKeys(options.Value.ApiKeys, false, ""), model, ct);
        foreach (var key in keys)
        {
            var socket = new ClientWebSocket();
            socket.Options.CollectHttpResponseDetails = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await socket.ConnectAsync(new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=" + Uri.EscapeDataString(key)), timeout.Token);
                await SendAsync(socket, Setup(model, BanteraAiIdentity.Prompt(user) + (metadata ?? new AiClientMetadata(new AiClock("UTC", 0), null)).TimePrompt, voiceMessage), timeout.Token);
                using var ready = await ReceiveJsonAsync(socket, timeout.Token);
                if (!ready.RootElement.TryGetProperty("setupComplete", out _)) throw new InvalidDataException("Live setup was not accepted.");
                if (history.Count > 0)
                    await SendAsync(socket, new { clientContent = new { turns = history.Select(t => new { role = t.Role, parts = new[] { new { text = t.Text } } }), turnComplete = false } }, timeout.Token);
                return socket;
            }
            catch (Exception ex)
            {
                var quota = (int)socket.HttpStatusCode == 429 || ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests };
                var status = socket.HttpStatusCode;
                var close = socket.CloseStatus;
                socket.Dispose();
                ct.ThrowIfCancellationRequested();
                // Never log exception text: a WebSocket exception can contain the key URL.
                logger.LogWarning("Bantera AI connection failed for {Model}: {ErrorType}.", model, ex.GetType().Name);
                if (!quota) throw new InvalidOperationException($"AI connection unavailable ({ex.GetType().Name}; status={(int)status}; close={close}).");
                health.CoolDown(key, model);
            }
        }
        throw new InvalidOperationException("AI connection unavailable.");
    }

    public async Task<AiVoiceReply> ReplyAsync(string model, User user, byte[] pcm,
        IReadOnlyList<AiContextTurn> history, CancellationToken ct, string? text = null, JsonElement snapshot = default, AiClientMetadata? metadata = null, Func<JsonElement, Task<object>>? executeTool = null)
    {
        using var socket = await ConnectAsync(model, user, true, history, ct, metadata);
        if (!string.IsNullOrWhiteSpace(text))
            await SendAsync(socket, new { clientContent = new { turns = new[] { new { role = "user", parts = new[] { new { text } } } }, turnComplete = true } }, ct);
        else
        {
        await SendAsync(socket, new { realtimeInput = new { activityStart = new { } } }, ct);
        for (var offset = 0; offset < pcm.Length; offset += 16000)
            await SendAudioAsync(socket, pcm.AsMemory(offset, Math.Min(16000, pcm.Length - offset)), ct);
        await SendAsync(socket, new { realtimeInput = new { activityEnd = new { } } }, ct);
        }
        using var audio = new MemoryStream();
        var input = new StringBuilder();
        var output = new StringBuilder();
        while (true)
        {
            using var json = await ReceiveJsonAsync(socket, ct);
            if (json.RootElement.TryGetProperty("toolCall", out var toolCall))
            {
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
                audio.Write(chunk);
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
        if (json.RootElement.TryGetProperty("error", out var error)) {
            var quota = error.TryGetProperty("code", out var code) && code.TryGetInt32(out var status) && status == 429;
            json.Dispose();
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
            if (part.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Session closed.");
            if (data.Length + part.Count > maxBytes) throw new InvalidDataException("Frame too large.");
            data.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return (data.ToArray(), part.MessageType);
    }
}

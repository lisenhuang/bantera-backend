using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BanteraApi.Database.Entities;

namespace BanteraApi.Chat.Ai;

// Only opaque provider handles and hashes are held in RAM, never transcripts,
// summaries or audio. A restart simply falls back to the device's context.
public static class AiLiveResumeCache
{
    public sealed record Entry(string Handle, string KeyHash, string LastReplyHash, DateTimeOffset ExpiresAt);
    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    private static readonly ConditionalWeakTable<WebSocket, State> Sockets = new();
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string? Identity(string model, User user, bool voiceMessage, AiClientMetadata? metadata, string voice, string? reasoning) =>
        Guid.TryParse(metadata?.ConversationId, out var conversation) ? Hash(JsonSerializer.Serialize(new {
            contextVersion = 5, user.Id, conversation, model, voiceMessage, voice, reasoning, user.LearningLanguage, user.NativeLanguage,
            user.Name, metadata.LearningLevel, metadata.DeviceWebSearch
        })) : null;
    public static string? Preferred(string? identity) => identity is not null && Entries.TryGetValue(identity, out var value) && value.ExpiresAt > DateTimeOffset.UtcNow ? value.KeyHash : null;
    // Match provider speech, not the app's attachment annotations. Old clients
    // lack ResumeText; accept their known display repairs without losing checks
    // against a missing/newer turn, account, profile, model or expired handle.
    public static string ReplyHash(string text)
    {
        var image = text.IndexOf("\n[Shared image:", StringComparison.Ordinal);
        if (image >= 0) text = text[..image];
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(`{3,}|~{3,})[\s\S]*?(?:\1|$)|(`+)[\s\S]*?\2|\\u(0027|0022|2018|2019|201c|201d)",
            m => m.Groups[3].Success ? char.ConvertFromUtf32(Convert.ToInt32(m.Groups[3].Value, 16)) : m.Value,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = AiConversationTiming.HistoryText(new("model", text));
        return Hash(text.Trim()[..Math.Min(text.Trim().Length, 2000)]);
    }
    public static Entry? Take(string? identity, string key, IReadOnlyList<AiContextTurn> history, Action<string>? observe = null)
    {
        Entry? Reject(string reason) { observe?.Invoke(reason); return null; }
        if (identity is null) return Reject("legacy_client");
        if (!Entries.TryRemove(identity, out var value)) return Reject("no_checkpoint");
        if (value.ExpiresAt <= DateTimeOffset.UtcNow) return Reject("expired");
        if (value.KeyHash != Hash(key)) return Reject("different_key");
        var last = history.LastOrDefault(t => t.CreatedAt is not null);
        if (last?.Role != "model") return Reject("latest_turn_not_reply");
        if (ReplyHash(last.ResumeText ?? last.Text) != value.LastReplyHash) return Reject("reply_changed");
        observe?.Invoke("resuming");
        return value;
    }
    public static void Attach(WebSocket socket, string? identity, string key, string model, bool resumed = false)
    {
        if (identity is not null) Sockets.Add(socket, new State(identity, Hash(key), AiLiveModelPolicy.RequiresInteractionIdle(model), resumed));
    }
    public static bool WasResumed(WebSocket socket) => Sockets.TryGetValue(socket, out var state) && state.Resumed;
    public static bool HasCheckpoint(WebSocket socket) => Sockets.TryGetValue(socket, out var state) && state.Idle && state.Handle is not null;
    public static void Observe(WebSocket socket, JsonElement message) { if (Sockets.TryGetValue(socket, out var state)) state.Observe(message); }
    public static void Release(WebSocket socket, bool successful = true)
    {
        if (!Sockets.TryGetValue(socket, out var state)) return;
        Sockets.Remove(socket);
        if (!successful || !state.Idle || state.Handle is null || state.LastReplyHash is null) return;
        foreach (var row in Entries.Where(x => x.Value.ExpiresAt <= DateTimeOffset.UtcNow)) Entries.TryRemove(row.Key, out _);
        if (Entries.Count >= 1024) return;
        Entries[state.Identity] = new(state.Handle, state.KeyHash, state.LastReplyHash, DateTimeOffset.UtcNow.AddHours(2));
    }
    public sealed class State(string identity, string keyHash, bool extended, bool resumed = false)
    {
        public bool Resumed { get; } = resumed;
        public string Identity { get; } = identity;
        public string KeyHash { get; } = keyHash;
        public string? Handle { get; private set; }
        public string? LastReplyHash { get; private set; }
        public bool Idle { get; private set; }
        private readonly AiLiveCompletion completion = new(extended);
        private readonly StringBuilder output = new();
        public void Observe(JsonElement message)
        {
            if (message.TryGetProperty("serverContent", out var content)) {
                if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.ValueKind == JsonValueKind.True) {
                    Idle = false; Handle = null; output.Clear(); LastReplyHash = null;
                }
                if (content.TryGetProperty("modelTurn", out _) || content.TryGetProperty("inputTranscription", out _) || content.TryGetProperty("outputTranscription", out _)) {
                    if (Idle) output.Clear();
                    Idle = false;
                }
                GeminiLiveService.AppendTranscript(content, "outputTranscription", output);
            }
            if (completion.Observe(message)) {
                // A handle observed before this reply completed may restore an older
                // checkpoint. Require a new resumable handle at/after completion.
                Handle = null;
                Idle = true;
                var text = output.ToString().Trim();
                LastReplyHash = text.Length == 0 ? null : ReplyHash(text);
            }
            if (message.TryGetProperty("sessionResumptionUpdate", out var update)) {
                Handle = update.TryGetProperty("resumable", out var resumable) && resumable.ValueKind == JsonValueKind.True &&
                    update.TryGetProperty("newHandle", out var handle) && handle.ValueKind == JsonValueKind.String &&
                    handle.GetString() is { Length: > 0 and <= 8192 } value ? value : null;
            }
        }
    }
}

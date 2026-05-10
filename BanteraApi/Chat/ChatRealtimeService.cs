using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BanteraApi.Chat;

public class ChatRealtimeService(ILogger<ChatRealtimeService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PendingCallTimeout = TimeSpan.FromSeconds(45);
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, WebSocket>> _connections = new();
    private readonly ConcurrentDictionary<Guid, ChatCallSession> _calls = new();

    public string Register(Guid userId, WebSocket socket)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        var bucket = _connections.GetOrAdd(userId, _ => new ConcurrentDictionary<string, WebSocket>());
        bucket[connectionId] = socket;
        return connectionId;
    }

    public void Unregister(Guid userId, string connectionId)
    {
        if (!_connections.TryGetValue(userId, out var bucket))
            return;

        bucket.TryRemove(connectionId, out _);
        if (bucket.IsEmpty)
            _connections.TryRemove(userId, out _);
    }

    public bool IsUserOnline(Guid userId)
    {
        return _connections.TryGetValue(userId, out var bucket)
            && bucket.Values.Any(socket => socket.State == WebSocketState.Open);
    }

    public bool TryCreateCall(
        Guid callerUserId,
        Guid calleeUserId,
        string mediaKind,
        out ChatCallSession session,
        bool requireCalleeOnline = true)
    {
        session = null!;
        if (!ChatCallMediaKinds.IsSupported(mediaKind))
            return false;

        if (requireCalleeOnline && !IsUserOnline(calleeUserId))
            return false;

        if (HasActiveCallFor(callerUserId) || HasActiveCallFor(calleeUserId))
            return false;

        session = new ChatCallSession(
            Guid.NewGuid(),
            callerUserId,
            calleeUserId,
            mediaKind.ToLowerInvariant(),
            DateTime.UtcNow);
        return _calls.TryAdd(session.CallId, session);
    }

    public bool TryAcceptCall(Guid callId, Guid userId, out ChatCallSession session)
    {
        session = null!;
        if (!_calls.TryGetValue(callId, out var current))
            return false;

        lock (current.SyncRoot)
        {
            if (current.State != ChatCallStateKinds.Pending || current.CalleeUserId != userId)
                return false;

            current.State = ChatCallStateKinds.Accepted;
            current.AcceptedAtUtc = DateTime.UtcNow;
            session = current.Clone();
            return true;
        }
    }

    public bool TryRejectCall(Guid callId, Guid userId, out ChatCallSession session)
    {
        return TryCompletePendingCall(callId, userId, allowCaller: false, out session);
    }

    public bool TryCancelCall(Guid callId, Guid userId, out ChatCallSession session)
    {
        return TryCompletePendingCall(callId, userId, allowCaller: true, out session);
    }

    public bool TryEndCall(Guid callId, Guid userId, out ChatCallSession session)
    {
        session = null!;
        if (!_calls.TryGetValue(callId, out var current))
            return false;

        lock (current.SyncRoot)
        {
            if (current.State == ChatCallStateKinds.Ended || !current.ContainsUser(userId))
                return false;

            current.State = ChatCallStateKinds.Ended;
            session = current.Clone();
        }

        _calls.TryRemove(callId, out _);
        return true;
    }

    public bool TryGetCall(Guid callId, Guid userId, out ChatCallSession session)
    {
        session = null!;
        if (!_calls.TryGetValue(callId, out var current))
            return false;

        lock (current.SyncRoot)
        {
            if (!current.ContainsUser(userId))
                return false;

            session = current.Clone();
            return true;
        }
    }

    public async Task HandleUserDisconnectedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (IsUserOnline(userId))
            return;

        var affected = new List<ChatCallSession>();
        foreach (var pair in _calls.ToArray())
        {
            var current = pair.Value;
            lock (current.SyncRoot)
            {
                if (current.State == ChatCallStateKinds.Ended || !current.ContainsUser(userId))
                    continue;

                current.State = ChatCallStateKinds.Ended;
                affected.Add(current.Clone());
            }

            _calls.TryRemove(pair.Key, out _);
        }

        foreach (var session in affected)
        {
            await SendToUserAsync(
                session.OtherUserId(userId),
                new
                {
                    type = "call.ended",
                    payload = new
                    {
                        callId = session.CallId,
                        reason = "peer_disconnected",
                    }
                },
                cancellationToken);
        }
    }

    public async Task PruneExpiredCallsAsync(CancellationToken cancellationToken = default)
    {
        var expired = new List<ChatCallSession>();
        var cutoff = DateTime.UtcNow - PendingCallTimeout;
        foreach (var pair in _calls.ToArray())
        {
            var current = pair.Value;
            lock (current.SyncRoot)
            {
                if (current.State != ChatCallStateKinds.Pending || current.CreatedAtUtc > cutoff)
                    continue;

                current.State = ChatCallStateKinds.Ended;
                expired.Add(current.Clone());
            }

            _calls.TryRemove(pair.Key, out _);
        }

        foreach (var session in expired)
        {
            await SendToUserAsync(
                session.CallerUserId,
                new
                {
                    type = "call.missed",
                    payload = new { callId = session.CallId, reason = "timeout" }
                },
                cancellationToken);

            await SendToUserAsync(
                session.CalleeUserId,
                new
                {
                    type = "call.cancelled",
                    payload = new { callId = session.CallId, reason = "timeout" }
                },
                cancellationToken);
        }
    }

    public static ChatIceServersResponse BuildDefaultIceServersResponse()
    {
        return new ChatIceServersResponse(
            [
                new ChatIceServerEntryResponse(
                    ["stun:stun.l.google.com:19302"],
                    null,
                    null),
            ]);
    }

    public HashSet<Guid> SnapshotOnlineUserIds()
    {
        return _connections
            .Where(pair => pair.Value.Values.Any(socket => socket.State == WebSocketState.Open))
            .Select(pair => pair.Key)
            .ToHashSet();
    }

    public Task SendToUserAsync(Guid userId, object payload, CancellationToken cancellationToken = default)
    {
        return SendToUsersAsync([userId], payload, cancellationToken);
    }

    public async Task SendToUsersAsync(IEnumerable<Guid> userIds, object payload, CancellationToken cancellationToken = default)
    {
        var message = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

        foreach (var userId in userIds.Distinct())
        {
            if (!_connections.TryGetValue(userId, out var bucket))
                continue;

            foreach (var pair in bucket.ToArray())
            {
                try
                {
                    if (pair.Value.State != WebSocketState.Open)
                    {
                        bucket.TryRemove(pair.Key, out _);
                        continue;
                    }

                    await pair.Value.SendAsync(
                        new ArraySegment<byte>(message),
                        WebSocketMessageType.Text,
                        true,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Removing failed chat websocket connection {ConnectionId}", pair.Key);
                    bucket.TryRemove(pair.Key, out _);
                }
            }

            if (bucket.IsEmpty)
                _connections.TryRemove(userId, out _);
        }
    }

    public static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var segment = new ArraySegment<byte>(buffer);
        await using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(segment, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.Count > 0)
                await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);

            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private bool HasActiveCallFor(Guid userId)
    {
        foreach (var current in _calls.Values)
        {
            lock (current.SyncRoot)
            {
                if (current.State != ChatCallStateKinds.Ended && current.ContainsUser(userId))
                    return true;
            }
        }

        return false;
    }

    private bool TryCompletePendingCall(Guid callId, Guid userId, bool allowCaller, out ChatCallSession session)
    {
        session = null!;
        if (!_calls.TryGetValue(callId, out var current))
            return false;

        lock (current.SyncRoot)
        {
            var isAuthorized = allowCaller
                ? current.CallerUserId == userId
                : current.CalleeUserId == userId;
            if (current.State != ChatCallStateKinds.Pending || !isAuthorized)
                return false;

            current.State = ChatCallStateKinds.Ended;
            session = current.Clone();
        }

        _calls.TryRemove(callId, out _);
        return true;
    }
}

public sealed class ChatCallSession(
    Guid callId,
    Guid callerUserId,
    Guid calleeUserId,
    string mediaKind,
    DateTime createdAtUtc)
{
    public object SyncRoot { get; } = new();
    public Guid CallId { get; } = callId;
    public Guid CallerUserId { get; } = callerUserId;
    public Guid CalleeUserId { get; } = calleeUserId;
    public string MediaKind { get; } = mediaKind;
    public DateTime CreatedAtUtc { get; } = createdAtUtc;
    public DateTime? AcceptedAtUtc { get; set; }
    public string State { get; set; } = ChatCallStateKinds.Pending;

    public bool ContainsUser(Guid userId) => CallerUserId == userId || CalleeUserId == userId;

    public Guid OtherUserId(Guid userId) => CallerUserId == userId ? CalleeUserId : CallerUserId;

    public ChatCallSession Clone()
    {
        return new ChatCallSession(CallId, CallerUserId, CalleeUserId, MediaKind, CreatedAtUtc)
        {
            AcceptedAtUtc = AcceptedAtUtc,
            State = State,
        };
    }
}

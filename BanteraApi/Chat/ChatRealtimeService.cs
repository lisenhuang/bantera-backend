using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BanteraApi.Chat;

public class ChatRealtimeService(ILogger<ChatRealtimeService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, WebSocket>> _connections = new();

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
}

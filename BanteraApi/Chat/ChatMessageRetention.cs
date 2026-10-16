using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Chat;

public static class ChatMessageRetention
{
    public static async Task DeleteExpiredAsync(AppDbContext db,
        Func<string, CancellationToken, Task> deleteAudio, ChatRealtimeService realtime, ILogger logger,
        IReadOnlyList<Guid>? onlyIds = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var query = db.ChatMessages.AsNoTracking()
            .Where(m => m.Thread.Type == ChatThreadTypes.DirectMessage && m.ExpiresAt <= now);
        if (onlyIds is not null) query = query.Where(m => onlyIds.Contains(m.Id));
        var messages = await query.OrderBy(m => m.ExpiresAt).Take(100)
            .Select(m => new { m.Id, m.ThreadId, m.AudioObjectKey }).ToListAsync(ct);
        foreach (var message in messages)
        {
            try { await deleteAudio(message.AudioObjectKey, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning("Chat audio cleanup deferred. MessageId={MessageId} Failure={Failure}", message.Id, ex.GetType().Name);
                continue;
            }
            // An idempotent SQL delete tolerates another cleanup worker winning.
            var deleted = await db.ChatMessages.Where(m => m.Id == message.Id && m.ExpiresAt <= now).ExecuteDeleteAsync(ct);
            if (deleted == 0) continue;
            var members = await db.ChatThreadMemberships.AsNoTracking()
                .Where(m => m.ThreadId == message.ThreadId).Select(m => m.UserId).ToListAsync(ct);
            // Expiry preserves durable local DM history; explicit deletion is a different event.
            await realtime.SendToUsersAsync(members,
                new { type = "message.expired", payload = new { messageId = message.Id, threadId = message.ThreadId } }, ct);
        }
    }
}

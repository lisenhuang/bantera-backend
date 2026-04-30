using BanteraApi.Database;
using BanteraApi.Storage;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Chat;

public class ChatCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<ChatCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupExpiredMessagesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat cleanup pass failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }

    private async Task CleanupExpiredMessagesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var r2Storage = scope.ServiceProvider.GetRequiredService<R2StorageService>();
        var realtime = scope.ServiceProvider.GetRequiredService<ChatRealtimeService>();

        var now = DateTime.UtcNow;
        var expired = await db.ChatMessages
            .Where(m => m.ExpiresAt != null && m.ExpiresAt <= now)
            .OrderBy(m => m.ExpiresAt)
            .Take(100)
            .Select(m => new
            {
                m.Id,
                m.ThreadId,
                m.AudioObjectKey,
            })
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
            return;

        foreach (var message in expired)
        {
            try
            {
                await r2Storage.DeleteObjectAsync(message.AudioObjectKey, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete expired chat audio object {Key}", message.AudioObjectKey);
            }
        }

        var expiredIds = expired.Select(x => x.Id).ToHashSet();
        var affectedThreadIds = expired.Select(x => x.ThreadId).Distinct().ToList();
        var affectedUsers = await db.ChatThreadMemberships
            .Where(m => affectedThreadIds.Contains(m.ThreadId))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var messages = await db.ChatMessages
            .Where(m => expiredIds.Contains(m.Id))
            .ToListAsync(cancellationToken);
        db.ChatMessages.RemoveRange(messages);
        await db.SaveChangesAsync(cancellationToken);

        var latestByThread = await db.ChatMessages
            .Where(m => affectedThreadIds.Contains(m.ThreadId))
            .GroupBy(m => m.ThreadId)
            .Select(g => new { ThreadId = g.Key, LastMessageAt = g.Max(x => x.CreatedAt) })
            .ToListAsync(cancellationToken);

        var latestMap = latestByThread.ToDictionary(x => x.ThreadId, x => (DateTime?)x.LastMessageAt);
        var threads = await db.ChatThreads
            .Where(t => affectedThreadIds.Contains(t.Id))
            .ToListAsync(cancellationToken);
        foreach (var thread in threads)
        {
            thread.LastMessageAt = latestMap.GetValueOrDefault(thread.Id);
            thread.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var message in expired)
        {
            await realtime.SendToUsersAsync(
                affectedUsers,
                new
                {
                    type = "message.expired",
                    payload = new { messageId = message.Id, threadId = message.ThreadId }
                },
                cancellationToken);
        }

        await realtime.SendToUsersAsync(
            affectedUsers,
            new
            {
                type = "thread.updated",
                payload = new { threadIds = affectedThreadIds }
            },
            cancellationToken);
    }
}

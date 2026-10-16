using BanteraApi.Database;
using BanteraApi.Database.Entities;
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

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task CleanupExpiredMessagesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var r2Storage = scope.ServiceProvider.GetRequiredService<R2StorageService>();
        var realtime = scope.ServiceProvider.GetRequiredService<ChatRealtimeService>();

        await ChatMessageRetention.DeleteExpiredAsync(db, r2Storage.DeleteObjectAsync, realtime,
            logger, ct: cancellationToken);
    }
}

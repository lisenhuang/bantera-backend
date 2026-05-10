namespace BanteraApi.Chat;

public class ChatCallCleanupService(
    ChatRealtimeService realtimeService,
    ILogger<ChatCallCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await realtimeService.PruneExpiredCallsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat call cleanup pass failed.");
            }

            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}

using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Mcp;

/// <summary>
/// Periodically purges spent OAuth state: authorization codes that expired more than a day
/// ago, and refresh tokens that expired or were revoked more than 30 days ago.
/// </summary>
public class McpOAuthCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<McpOAuthCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup finish before the first sweep.
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;

                var codes = await db.OAuthAuthorizationCodes
                    .Where(c => c.ExpiresAt < now.AddDays(-1))
                    .ExecuteDeleteAsync(stoppingToken);

                var tokens = await db.OAuthRefreshTokens
                    .Where(t => t.ExpiresAt < now.AddDays(-30)
                             || (t.RevokedAt != null && t.RevokedAt < now.AddDays(-30)))
                    .ExecuteDeleteAsync(stoppingToken);

                if (codes > 0 || tokens > 0)
                    logger.LogInformation("[MCP OAuth] Cleanup removed {Codes} codes and {Tokens} refresh tokens", codes, tokens);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[MCP OAuth] Cleanup failed");
            }

            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) { break; }
        }
    }
}

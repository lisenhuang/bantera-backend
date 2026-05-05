using System.Collections.Concurrent;
using System.Threading.Channels;

namespace BanteraApi.Profile;

public sealed class GeneratedAvatarQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<GeneratedAvatarQueue> logger) : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    public bool Enqueue(Guid userId)
    {
        if (!_pending.TryAdd(userId, 0))
            return false;

        if (_queue.Writer.TryWrite(userId))
            return true;

        _pending.TryRemove(userId, out _);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var userId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var profileService = scope.ServiceProvider.GetRequiredService<ProfileService>();
                var generated = await profileService.GenerateMissingAvatarAsync(userId, stoppingToken);
                if (generated)
                {
                    logger.LogInformation("[GeneratedAvatar] Generated profile image. UserId={UserId}", userId);
                }
                else
                {
                    logger.LogInformation("[GeneratedAvatar] Skipped profile image generation. UserId={UserId}", userId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[GeneratedAvatar] Failed to generate profile image. UserId={UserId}", userId);
            }
            finally
            {
                _pending.TryRemove(userId, out _);
            }
        }
    }
}

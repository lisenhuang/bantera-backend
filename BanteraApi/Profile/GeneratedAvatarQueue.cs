using System.Collections.Concurrent;
using System.Threading.Channels;

namespace BanteraApi.Profile;

public sealed class GeneratedAvatarQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<GeneratedAvatarQueue> logger) : BackgroundService
{
    private readonly Channel<GeneratedAvatarJob> _queue = Channel.CreateUnbounded<GeneratedAvatarJob>();
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    public bool Enqueue(Guid userId, string avatarGender)
    {
        if (!_pending.TryAdd(userId, 0))
            return false;

        if (_queue.Writer.TryWrite(new GeneratedAvatarJob(userId, avatarGender)))
            return true;

        _pending.TryRemove(userId, out _);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var profileService = scope.ServiceProvider.GetRequiredService<ProfileService>();
                var generated = await profileService.GenerateMissingAvatarAsync(
                    job.UserId,
                    job.AvatarGender,
                    stoppingToken);
                if (generated)
                {
                    logger.LogInformation("[GeneratedAvatar] Generated profile image. UserId={UserId}", job.UserId);
                }
                else
                {
                    logger.LogInformation("[GeneratedAvatar] Skipped profile image generation. UserId={UserId}", job.UserId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[GeneratedAvatar] Failed to generate profile image. UserId={UserId}", job.UserId);
            }
            finally
            {
                _pending.TryRemove(job.UserId, out _);
            }
        }
    }

    private sealed record GeneratedAvatarJob(Guid UserId, string AvatarGender);
}

using BanteraApi.Jobs;
namespace BanteraApi.Profile;

// Persist before acknowledging, so queued avatars survive a process restart.
public sealed class GeneratedAvatarQueue(IServiceScopeFactory scopes)
{
    public async Task<bool> EnqueueAsync(Guid userId, string avatarGender, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OperationService>().QueueInternalAsync(userId, "avatar",
            $"{avatarGender}/{DateTime.UtcNow:yyyy-MM-dd}", new { avatarGender }, ct);
        return true;
    }
}

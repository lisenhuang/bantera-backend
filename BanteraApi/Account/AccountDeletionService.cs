using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Storage;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Account;

public class AccountDeletionService(
    AppDbContext db,
    R2StorageService r2StorageService,
    ILogger<AccountDeletionService> logger)
{
    private static readonly Guid BanteraAiUserId = new("816cd28a-7629-4400-948b-4e0b65bd3638");
    private static readonly DateTime BanteraAiSeededAt = new(2026, 4, 14, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Permanently deletes the user and dependent rows while preserving AI-generated
    /// audio by reassigning it to the Bantera AI placeholder owner.
    /// </summary>
    /// <returns>False if the user did not exist.</returns>
    public async Task<bool> DeleteAccountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users
            .Include(u => u.Videos)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
            return false;

        var videosToPreserve = user.Videos
            .Where(ShouldPreserveAiAudio)
            .ToList();
        var objectKeysToDelete = user.Videos
            .Where(video => !ShouldPreserveAiAudio(video))
            .SelectMany(GetStorageKeysToDelete)
            .ToList();
        var chatAudioKeysToDelete = await db.ChatMessages
            .Where(m => m.SenderUserId == userId)
            .Select(m => m.AudioObjectKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToListAsync(cancellationToken);
        var avatarKeyToDelete = user.AvatarObjectKey;

        if (videosToPreserve.Count > 0)
        {
            var banteraAiUser = await EnsureBanteraAiUserAsync(cancellationToken);
            var now = DateTime.UtcNow;

            foreach (var video in videosToPreserve)
            {
                video.UserId = banteraAiUser.Id;
                video.User = banteraAiUser;
                video.RemovedFromOwnerListAt = null;
                video.UpdatedAt = now;
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            if (videosToPreserve.Count > 0)
                await db.SaveChangesAsync(cancellationToken);

            db.Users.Remove(user);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        foreach (var key in objectKeysToDelete)
            await SafeDeleteObjectAsync(key, cancellationToken);

        foreach (var key in chatAudioKeysToDelete)
            await SafeDeleteObjectAsync(key, cancellationToken);

        if (!string.IsNullOrEmpty(avatarKeyToDelete))
            await SafeDeleteObjectAsync(avatarKeyToDelete, cancellationToken);

        return true;
    }

    private async Task<User> EnsureBanteraAiUserAsync(CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == BanteraAiUserId, cancellationToken);
        if (user is not null)
            return user;

        user = new User
        {
            Id = BanteraAiUserId,
            Name = "Bantera AI",
            Role = "system",
            Status = "system",
            CreatedAt = BanteraAiSeededAt,
            UpdatedAt = BanteraAiSeededAt,
            DeletedAt = BanteraAiSeededAt,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    private static bool ShouldPreserveAiAudio(UserVideo video) =>
        video.IsAiGenerated &&
        video.MediaContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> GetStorageKeysToDelete(UserVideo video)
    {
        if (!string.IsNullOrWhiteSpace(video.MediaObjectKey))
            yield return video.MediaObjectKey;

        if (!string.IsNullOrWhiteSpace(video.CoverImageObjectKey))
            yield return video.CoverImageObjectKey!;
    }

    private async Task SafeDeleteObjectAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await r2StorageService.DeleteObjectAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "R2 delete failed during account deletion for key {Key}", key);
        }
    }
}

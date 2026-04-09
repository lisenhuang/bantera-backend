using BanteraApi.Database;
using BanteraApi.Storage;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Account;

public class AccountDeletionService(
    AppDbContext db,
    R2StorageService r2StorageService,
    ILogger<AccountDeletionService> logger)
{
    /// <summary>
    /// Permanently deletes the user and dependent rows. Deletes R2 objects only for
    /// user-uploaded videos (!IsAiGenerated) and the user's avatar; skips R2 for AI-generated media.
    /// </summary>
    /// <returns>False if the user did not exist.</returns>
    public async Task<bool> DeleteAccountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users
            .Include(u => u.Videos)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
            return false;

        foreach (var video in user.Videos)
        {
            if (video.IsAiGenerated)
                continue;

            await SafeDeleteObjectAsync(video.MediaObjectKey, cancellationToken);
            if (!string.IsNullOrEmpty(video.CoverImageObjectKey))
                await SafeDeleteObjectAsync(video.CoverImageObjectKey!, cancellationToken);
        }

        if (!string.IsNullOrEmpty(user.AvatarObjectKey))
            await SafeDeleteObjectAsync(user.AvatarObjectKey, cancellationToken);

        db.Users.Remove(user);
        await db.SaveChangesAsync(cancellationToken);
        return true;
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

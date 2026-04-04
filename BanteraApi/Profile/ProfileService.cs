using System.Net;
using BanteraApi.Auth;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Profile;

public class ProfileService(
    AppDbContext db,
    R2StorageService r2StorageService,
    LinkGenerator linkGenerator,
    ILogger<ProfileService> logger)
{
    private static readonly HashSet<string> SupportedImageContentTypes =
    [
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/heic",
        "image/heif",
    ];

    private const long MaxAvatarBytes = 5 * 1024 * 1024;

    public async Task<UserProfileResponse?> GetProfileAsync(
        Guid userId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var user = await LoadUserAsync(userId, cancellationToken);
        return user is null ? null : BuildResponse(user, httpContext);
    }

    public async Task<(UserProfileResponse? Response, string? ErrorCode)> UpdateNameAsync(
        Guid userId,
        string name,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 80)
            return (null, ErrorCodes.InvalidProfile);

        var user = await LoadUserAsync(userId, cancellationToken);
        if (user is null)
            return (null, ErrorCodes.Unauthorized);

        user.Name = normalizedName;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return (BuildResponse(user, httpContext), null);
    }

    public async Task<(UserProfileResponse? Response, string? ErrorCode)> UpdateProfileAsync(
        Guid userId,
        string? name,
        string? translationLanguage,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var hasName = name is not null;
        var hasTranslationLanguage = translationLanguage is not null;
        if (!hasName && !hasTranslationLanguage)
            return (null, ErrorCodes.InvalidProfile);

        var normalizedName = name?.Trim();
        if (hasName && (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 80))
            return (null, ErrorCodes.InvalidProfile);

        var normalizedTranslationLanguage = NormalizeTranslationLanguage(translationLanguage);
        if (hasTranslationLanguage && normalizedTranslationLanguage is null)
            return (null, ErrorCodes.InvalidProfile);

        var user = await LoadUserAsync(userId, cancellationToken);
        if (user is null)
            return (null, ErrorCodes.Unauthorized);

        if (hasName)
            user.Name = normalizedName;
        if (hasTranslationLanguage)
            user.TranslationLanguage = normalizedTranslationLanguage;

        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return (BuildResponse(user, httpContext), null);
    }

    public async Task<(UserProfileResponse? Response, string? ErrorCode)> UpdateAvatarAsync(
        Guid userId,
        IFormFile file,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (file.Length == 0 || file.Length > MaxAvatarBytes)
            return (null, ErrorCodes.InvalidProfileImage);

        var contentType = NormalizeContentType(file.ContentType);
        if (contentType is null)
            return (null, ErrorCodes.InvalidProfileImage);

        var user = await LoadUserAsync(userId, cancellationToken);
        if (user is null)
            return (null, ErrorCodes.Unauthorized);

        var oldKey = user.AvatarObjectKey;
        var newKey = BuildAvatarObjectKey(userId, contentType);

        await using var input = file.OpenReadStream();
        await r2StorageService.UploadObjectAsync(newKey, input, contentType, cancellationToken);

        user.AvatarObjectKey = newKey;
        user.AvatarUpdatedAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(oldKey) && !string.Equals(oldKey, newKey, StringComparison.Ordinal))
        {
            try
            {
                await r2StorageService.DeleteObjectAsync(oldKey, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete old avatar object {Key}", oldKey);
            }
        }

        return (BuildResponse(user, httpContext), null);
    }

    public async Task<StoredObjectResult?> GetAvatarAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var avatarKey = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.AvatarObjectKey)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(avatarKey))
            return null;

        return await r2StorageService.DownloadObjectAsync(avatarKey, cancellationToken);
    }

    private async Task<User?> LoadUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await db.Users
            .Include(u => u.Identities)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
    }

    private UserProfileResponse BuildResponse(User user, HttpContext httpContext)
    {
        return new UserProfileResponse(
            user.Id,
            ResolveName(user),
            BuildAvatarUrl(user, httpContext),
            user.TranslationLanguage);
    }

    private string ResolveName(User user)
    {
        if (!string.IsNullOrWhiteSpace(user.Name))
            return user.Name;

        var emailIdentity = user.Identities
            .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.ProviderEmail));

        if (!string.IsNullOrWhiteSpace(emailIdentity?.ProviderEmail))
        {
            var email = emailIdentity.ProviderEmail!;
            var atIndex = email.IndexOf('@');
            if (atIndex > 0)
                return email[..atIndex];
            return email;
        }

        return "Bantera user";
    }

    private string? BuildAvatarUrl(User user, HttpContext httpContext)
    {
        if (string.IsNullOrWhiteSpace(user.AvatarObjectKey))
            return null;

        return linkGenerator.GetUriByName(
            httpContext,
            "GetUserAvatar",
            values: new
            {
                userId = user.Id,
                v = user.AvatarUpdatedAt?.Ticks
            });
    }

    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;

        var normalized = contentType.Trim().ToLowerInvariant();
        return SupportedImageContentTypes.Contains(normalized)
            ? normalized
            : null;
    }

    private static string? NormalizeTranslationLanguage(string? translationLanguage)
    {
        if (translationLanguage is null)
            return null;

        var normalized = translationLanguage.Trim().Replace('_', '-');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 35)
            return null;

        foreach (var segment in normalized.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length > 8 || segment.Any(ch => !char.IsLetterOrDigit(ch)))
                return null;
        }

        return normalized;
    }

    private static string BuildAvatarObjectKey(Guid userId, string contentType)
    {
        var extension = contentType switch
        {
            "image/png" => "png",
            "image/webp" => "webp",
            "image/heic" => "heic",
            "image/heif" => "heif",
            _ => "jpg",
        };

        return $"avatars/{userId}/{Guid.NewGuid():N}.{extension}";
    }
}

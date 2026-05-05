using System.Net;
using BanteraApi.Auth;
using BanteraApi.Cloudflare;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace BanteraApi.Profile;

public class ProfileService(
    AppDbContext db,
    R2StorageService r2StorageService,
    CloudflareImageService cloudflareImageService,
    LinkGenerator linkGenerator,
    ILogger<ProfileService> logger,
    IMemoryCache memoryCache)
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
        string? nativeLanguage,
        string? learningLanguage,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        // Whitespace-only strings are treated as "key omitted" for name and
        // translation language so clients that echo empty fields never block
        // a valid single-field update (e.g. learning language onboarding).
        if (name is not null && string.IsNullOrWhiteSpace(name))
            name = null;
        if (translationLanguage is not null && string.IsNullOrWhiteSpace(translationLanguage))
            translationLanguage = null;

        var hasName = name is not null;
        var hasTranslationLanguage = translationLanguage is not null;
        var hasNativeLanguage = nativeLanguage is not null;
        var hasLearningLanguage = learningLanguage is not null;
        if (!hasName && !hasTranslationLanguage && !hasNativeLanguage && !hasLearningLanguage)
            return (null, ErrorCodes.InvalidProfile);

        var normalizedName = name?.Trim();
        if (hasName && (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 80))
            return (null, ErrorCodes.InvalidProfile);

        var normalizedTranslationLanguage = NormalizeTranslationLanguage(translationLanguage);
        if (hasTranslationLanguage && normalizedTranslationLanguage is null)
            return (null, ErrorCodes.InvalidProfile);

        // For native/learning language an empty string means "clear the field".
        var normalizedNativeLanguage = NormalizeLanguageOrClear(nativeLanguage, out var nativeIsInvalid);
        if (hasNativeLanguage && nativeIsInvalid)
            return (null, ErrorCodes.InvalidProfile);

        var normalizedLearningLanguage = NormalizeLanguageOrClear(learningLanguage, out var learningIsInvalid);
        if (hasLearningLanguage && learningIsInvalid)
            return (null, ErrorCodes.InvalidProfile);

        var user = await LoadUserAsync(userId, cancellationToken);
        if (user is null)
            return (null, ErrorCodes.Unauthorized);

        if (hasName)
            user.Name = normalizedName;
        if (hasTranslationLanguage)
            user.TranslationLanguage = normalizedTranslationLanguage;
        if (hasNativeLanguage)
            user.NativeLanguage = normalizedNativeLanguage;
        if (hasLearningLanguage)
            user.LearningLanguage = normalizedLearningLanguage;

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

        memoryCache.Remove(AvatarObjectKeyCacheKey(userId));

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

    public async Task<AvatarGenerationReadiness> GetAvatarGenerationReadinessAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await LoadUserAsync(userId, cancellationToken);
        if (user is null)
            return AvatarGenerationReadiness.NotFound;

        if (!string.IsNullOrWhiteSpace(user.AvatarObjectKey))
            return AvatarGenerationReadiness.AlreadyExists;

        return HasRequiredAvatarPromptFields(user)
            ? AvatarGenerationReadiness.Ready
            : AvatarGenerationReadiness.MissingRequiredProfile;
    }

    public async Task<bool> GenerateMissingAvatarAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await LoadUserAsync(userId, cancellationToken);
        if (user is null)
            return false;

        if (!string.IsNullOrWhiteSpace(user.AvatarObjectKey))
            return false;

        if (!HasRequiredAvatarPromptFields(user))
            return false;

        var name = ResolveName(user).Trim();
        var nativeLanguage = user.NativeLanguage!.Trim();
        var learningLanguage = user.LearningLanguage!.Trim();
        var prompt = BuildGeneratedAvatarPrompt(name, nativeLanguage, learningLanguage);

        var pngBytes = await cloudflareImageService.GenerateImageAsync(prompt, cancellationToken);
        using var image = Image.Load(pngBytes);
        await using var jpegStream = new MemoryStream();
        await image.SaveAsJpegAsync(jpegStream, new JpegEncoder { Quality = 85 }, cancellationToken);
        jpegStream.Position = 0;

        await db.Entry(user).ReloadAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(user.AvatarObjectKey))
            return false;

        await StoreAvatarAsync(
            user,
            jpegStream,
            "image/jpeg",
            cancellationToken);

        return true;
    }

    public async Task<StoredObjectResult?> GetAvatarAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Single DB lookup per user per TTL even when many HTTP clients hit /avatar at once.
        var avatarKey = await memoryCache.GetOrCreateAsync(
            AvatarObjectKeyCacheKey(userId),
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                var key = await db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => u.AvatarObjectKey)
                    .FirstOrDefaultAsync(cancellationToken);
                return key ?? string.Empty;
            });

        if (string.IsNullOrWhiteSpace(avatarKey))
            return null;

        return await r2StorageService.DownloadObjectAsync(avatarKey, cancellationToken);
    }

    private static string AvatarObjectKeyCacheKey(Guid userId) => $"avatar:object-key:{userId:N}";

    private async Task StoreAvatarAsync(
        User user,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        var oldKey = user.AvatarObjectKey;
        var newKey = BuildAvatarObjectKey(user.Id, contentType);

        await r2StorageService.UploadObjectAsync(newKey, content, contentType, cancellationToken);

        user.AvatarObjectKey = newKey;
        user.AvatarUpdatedAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        memoryCache.Remove(AvatarObjectKeyCacheKey(user.Id));

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
            user.TranslationLanguage,
            user.NativeLanguage,
            user.LearningLanguage);
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

    private static bool HasRequiredAvatarPromptFields(User user)
    {
        return !string.IsNullOrWhiteSpace(user.Name)
            && !string.IsNullOrWhiteSpace(user.NativeLanguage)
            && !string.IsNullOrWhiteSpace(user.LearningLanguage);
    }

    private static string BuildGeneratedAvatarPrompt(
        string name,
        string nativeLanguage,
        string learningLanguage)
    {
        return
            $"Create a polished square profile avatar for Bantera. The person is named \"{name}\", " +
            $"a native {nativeLanguage} speaker learning {learningLanguage}. Friendly modern digital illustration, " +
            "head-and-shoulders avatar, warm approachable expression, subtle language-learning symbols such as speech bubbles or a small book, " +
            "tasteful hints of both languages without flags or stereotypes, clean light background, centered face, high contrast, app-profile quality. " +
            "No words, no letters, no logo, no watermark, no celebrity likeness.";
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

    /// Returns null (clear) when value is empty, the normalized BCP-47 string
    /// when valid, or sets <paramref name="isInvalid"/> = true when the value
    /// is non-empty but malformed.
    private static string? NormalizeLanguageOrClear(string? value, out bool isInvalid)
    {
        isInvalid = false;
        if (value is null) return null;        // null → not provided
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;  // empty → clear (null in DB)
        var normalized = NormalizeTranslationLanguage(value);
        if (normalized is null) { isInvalid = true; return null; }
        return normalized;
    }

    private static string? NormalizeTranslationLanguage(string? translationLanguage)
    {
        if (translationLanguage is null)
            return null;

        var normalized = translationLanguage.Trim().Replace('_', '-');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 35)
            return null;

        // BCP-47 variant and extension subtags are at most 8 alphanum (RFC 5646),
        // but Apple / platform locale identifiers occasionally use slightly
        // longer segments — keep a forgiving cap so valid iOS Speech locales pass.
        foreach (var segment in normalized.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length > 16 || segment.Any(ch => !char.IsLetterOrDigit(ch)))
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

public enum AvatarGenerationReadiness
{
    Ready,
    AlreadyExists,
    MissingRequiredProfile,
    NotFound
}

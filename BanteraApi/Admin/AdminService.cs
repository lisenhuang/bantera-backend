using BanteraApi.Database;
using BanteraApi.Storage;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Admin;

// ── Response types ────────────────────────────────────────────────────────────

public record AdminPagedResult<T>(IReadOnlyList<T> Items, int Total);

public record AdminUserListItem(
    Guid Id,
    string? Name,
    string? Email,
    string Role,
    string Status,
    string? NativeLanguage,
    string? LearningLanguage,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    int VideoCount);

public record AdminIdentityInfo(string Provider, string? ProviderEmail, DateTime CreatedAt);

public record AdminUserStats(int UploadCount, int VideoCount);

public record AdminUserDetail(
    Guid Id,
    string? Name,
    string Role,
    string Status,
    string? NativeLanguage,
    string? LearningLanguage,
    string? TranslationLanguage,
    int? AiAudioDailyLimit,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    IReadOnlyList<AdminIdentityInfo> Identities,
    AdminUserStats Stats);

public record AdminVideoListItem(
    Guid Id,
    Guid UserId,
    string? CreatorName,
    string OriginalFileName,
    string TranscriptLanguageCode,
    bool IsPublic,
    bool IsAiGenerated,
    int DurationMs,
    long FileSizeBytes,
    DateTime CreatedAt);

public record AdminUserBrief(Guid Id, string? Name, string? Email);

public record AdminChatMessageListItem(
    Guid Id,
    Guid ThreadId,
    string ThreadType,
    AdminUserBrief Sender,
    AdminUserBrief? Recipient,
    string? GroupLanguageKey,
    string? GroupLanguageDisplayName,
    int DurationMs,
    string SpokenLanguageCode,
    string OriginalFileName,
    string AudioContentType,
    DateTime CreatedAt,
    DateTime? ExpiresAt);

public record AdminStats(
    int TotalUsers,
    int TotalVideos,
    int ActiveLast7Days,
    int ActiveLast30Days,
    int AiGeneratedVideos,
    int UploadedVideos);

// ── Service ───────────────────────────────────────────────────────────────────

public class AdminService(AppDbContext db, R2StorageService r2)
{
    public async Task<AdminPagedResult<AdminUserListItem>> ListUsersAsync(
        string? search,
        string? sort,
        string? dir,
        int limit,
        int offset)
    {
        var query = db.Users
            .Where(u => u.DeletedAt == null)
            .GroupJoin(
                db.UserIdentities.Where(i => i.Provider == "email"),
                u => u.Id,
                i => i.UserId,
                (u, identities) => new { User = u, Identities = identities })
            .SelectMany(
                x => x.Identities.DefaultIfEmpty(),
                (x, identity) => new
                {
                    x.User,
                    Email = identity != null ? identity.ProviderEmail : null,
                    VideoCount = x.User.Videos.Count(v => v.UserId == x.User.Id),
                });

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            query = query.Where(x =>
                (x.User.Name != null && x.User.Name.ToLower().Contains(s)) ||
                (x.Email != null && x.Email.ToLower().Contains(s)));
        }

        var total = await query.CountAsync();

        query = (sort?.ToLower(), dir?.ToLower() == "desc") switch
        {
            ("name", false)          => query.OrderBy(x => x.User.Name),
            ("name", true)           => query.OrderByDescending(x => x.User.Name),
            ("email", false)         => query.OrderBy(x => x.Email),
            ("email", true)          => query.OrderByDescending(x => x.Email),
            ("status", false)        => query.OrderBy(x => x.User.Status),
            ("status", true)         => query.OrderByDescending(x => x.User.Status),
            ("role", false)          => query.OrderBy(x => x.User.Role),
            ("role", true)           => query.OrderByDescending(x => x.User.Role),
            ("nativelanguage", false)    => query.OrderBy(x => x.User.NativeLanguage),
            ("nativelanguage", true)     => query.OrderByDescending(x => x.User.NativeLanguage),
            ("learninglanguage", false)  => query.OrderBy(x => x.User.LearningLanguage),
            ("learninglanguage", true)   => query.OrderByDescending(x => x.User.LearningLanguage),
            ("lastloginat", false)   => query.OrderBy(x => x.User.LastLoginAt),
            ("lastloginat", true)    => query.OrderByDescending(x => x.User.LastLoginAt),
            ("videocount", false)    => query.OrderBy(x => x.VideoCount),
            ("videocount", true)     => query.OrderByDescending(x => x.VideoCount),
            // default: newest first
            _                        => query.OrderByDescending(x => x.User.CreatedAt),
        };

        var items = await query
            .Skip(offset)
            .Take(limit)
            .Select(x => new AdminUserListItem(
                x.User.Id,
                x.User.Name,
                x.Email,
                x.User.Role,
                x.User.Status,
                x.User.NativeLanguage,
                x.User.LearningLanguage,
                x.User.CreatedAt,
                x.User.LastLoginAt,
                x.VideoCount))
            .ToListAsync();

        return new AdminPagedResult<AdminUserListItem>(items, total);
    }

    public async Task<AdminUserDetail?> GetUserDetailAsync(Guid userId)
    {
        var user = await db.Users
            .Include(u => u.Identities)
            .FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null);

        if (user is null) return null;

        var videoCount = await db.UserVideos.CountAsync(v => v.UserId == userId);

        var identities = user.Identities
            .Select(i => new AdminIdentityInfo(i.Provider, i.ProviderEmail, i.CreatedAt))
            .ToList();

        return new AdminUserDetail(
            user.Id,
            user.Name,
            user.Role,
            user.Status,
            user.NativeLanguage,
            user.LearningLanguage,
            user.TranslationLanguage,
            user.AiAudioDailyLimit,
            user.CreatedAt,
            user.LastLoginAt,
            identities,
            new AdminUserStats(videoCount, videoCount));
    }

    public async Task<bool> PatchUserAsync(
        Guid userId,
        string? role,
        string? status,
        int? aiAudioDailyLimit,
        bool clearAiLimit)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null);
        if (user is null) return false;

        if (role is not null) user.Role = role;
        if (status is not null) user.Status = status;
        if (clearAiLimit) user.AiAudioDailyLimit = null;
        else if (aiAudioDailyLimit.HasValue) user.AiAudioDailyLimit = aiAudioDailyLimit.Value;

        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteUserAsync(Guid userId)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return false;

        db.Users.Remove(user);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<AdminStats> GetStatsAsync()
    {
        var now = DateTime.UtcNow;
        var cutoff7 = now.AddDays(-7);
        var cutoff30 = now.AddDays(-30);

        var totalUsers = await db.Users.CountAsync(u => u.DeletedAt == null);
        var totalVideos = await db.UserVideos.CountAsync();
        var activeLast7 = await db.Users.CountAsync(u =>
            u.DeletedAt == null && u.LastLoginAt != null && u.LastLoginAt >= cutoff7);
        var activeLast30 = await db.Users.CountAsync(u =>
            u.DeletedAt == null && u.LastLoginAt != null && u.LastLoginAt >= cutoff30);
        var aiGenerated = await db.UserVideos.CountAsync(v => v.IsAiGenerated);
        var uploaded = await db.UserVideos.CountAsync(v => !v.IsAiGenerated);

        return new AdminStats(totalUsers, totalVideos, activeLast7, activeLast30, aiGenerated, uploaded);
    }

    public async Task<AdminPagedResult<AdminVideoListItem>> ListVideosAsync(
        string? languageCode,
        bool? isPublic,
        bool? isAiGenerated,
        string? sort,
        string? dir,
        int limit,
        int offset)
    {
        var query = db.UserVideos
            .Include(v => v.User)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(languageCode))
            query = query.Where(v => v.TranscriptLanguageCode == languageCode);
        if (isPublic.HasValue)
            query = query.Where(v => v.IsPublic == isPublic.Value);
        if (isAiGenerated.HasValue)
            query = query.Where(v => v.IsAiGenerated == isAiGenerated.Value);

        var total = await query.CountAsync();

        query = (sort?.ToLower(), dir?.ToLower() == "desc") switch
        {
            ("filename", false)      => query.OrderBy(v => v.OriginalFileName),
            ("filename", true)       => query.OrderByDescending(v => v.OriginalFileName),
            ("language", false)      => query.OrderBy(v => v.TranscriptLanguageCode),
            ("language", true)       => query.OrderByDescending(v => v.TranscriptLanguageCode),
            ("duration", false)      => query.OrderBy(v => v.DurationMs),
            ("duration", true)       => query.OrderByDescending(v => v.DurationMs),
            ("size", false)          => query.OrderBy(v => v.FileSizeBytes),
            ("size", true)           => query.OrderByDescending(v => v.FileSizeBytes),
            _                        => query.OrderByDescending(v => v.CreatedAt),
        };

        var items = await query
            .Skip(offset)
            .Take(limit)
            .Select(v => new AdminVideoListItem(
                v.Id,
                v.UserId,
                v.User.Name,
                v.OriginalFileName,
                v.TranscriptLanguageCode,
                v.IsPublic,
                v.IsAiGenerated,
                v.DurationMs,
                v.FileSizeBytes,
                v.CreatedAt))
            .ToListAsync();

        return new AdminPagedResult<AdminVideoListItem>(items, total);
    }

    public async Task<bool> DeleteVideoAsync(Guid videoId)
    {
        var video = await db.UserVideos.FirstOrDefaultAsync(v => v.Id == videoId);
        if (video is null) return false;

        db.UserVideos.Remove(video);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<AdminPagedResult<AdminChatMessageListItem>> ListChatMessagesAsync(
        string? threadType,
        DateTime? from,
        DateTime? to,
        int limit,
        int offset)
    {
        var query = db.ChatMessages
            .Include(m => m.Thread)
                .ThenInclude(t => t.Memberships)
                    .ThenInclude(mb => mb.User)
                        .ThenInclude(u => u.Identities)
            .Include(m => m.SenderUser)
                .ThenInclude(u => u.Identities)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(threadType))
            query = query.Where(m => m.Thread.Type == threadType);
        if (from.HasValue)
            query = query.Where(m => m.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(m => m.CreatedAt <= to.Value);

        var total = await query.CountAsync();

        var messages = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();

        var items = messages.Select(m =>
        {
            var senderEmail = m.SenderUser.Identities
                .FirstOrDefault(i => i.Provider == "email")?.ProviderEmail;
            var sender = new AdminUserBrief(m.SenderUserId, m.SenderUser.Name, senderEmail);

            AdminUserBrief? recipient = null;
            string? groupLanguageKey = null;
            string? groupLanguageDisplayName = null;

            if (m.Thread.Type == "dm")
            {
                var recipientMembership = m.Thread.Memberships
                    .FirstOrDefault(mb => mb.UserId != m.SenderUserId);
                if (recipientMembership is not null)
                {
                    var recipientEmail = recipientMembership.User.Identities
                        .FirstOrDefault(i => i.Provider == "email")?.ProviderEmail;
                    recipient = new AdminUserBrief(
                        recipientMembership.UserId,
                        recipientMembership.User.Name,
                        recipientEmail);
                }
            }
            else
            {
                groupLanguageKey = m.Thread.LanguageKey;
                groupLanguageDisplayName = m.Thread.LanguageDisplayName;
            }

            return new AdminChatMessageListItem(
                m.Id,
                m.ThreadId,
                m.Thread.Type,
                sender,
                recipient,
                groupLanguageKey,
                groupLanguageDisplayName,
                m.DurationMs,
                m.SpokenLanguageCode,
                m.OriginalFileName,
                m.AudioContentType,
                m.CreatedAt,
                m.ExpiresAt);
        }).ToList();

        return new AdminPagedResult<AdminChatMessageListItem>(items, total);
    }

    public async Task<StoredObjectResult?> GetChatMessageAudioAsync(
        Guid messageId,
        CancellationToken ct)
    {
        var message = await db.ChatMessages.FindAsync([messageId], ct);
        if (message is null) return null;

        return await r2.DownloadObjectAsync(message.AudioObjectKey, ct);
    }

    public async Task<bool> DeleteChatMessageAsync(Guid messageId, CancellationToken ct)
    {
        var message = await db.ChatMessages
            .Include(m => m.Receipts)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null) return false;

        await r2.DeleteObjectAsync(message.AudioObjectKey, ct);

        db.ChatMessageReceipts.RemoveRange(message.Receipts);
        db.ChatMessages.Remove(message);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

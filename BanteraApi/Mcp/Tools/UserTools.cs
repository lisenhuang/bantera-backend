using System.ComponentModel;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace BanteraApi.Mcp.Tools;

/// <summary>
/// User lookup and segmentation. These read personal data, so calls are written to the
/// audit log.
/// </summary>
[McpServerToolType]
public sealed class UserTools(AppDbContext db, McpAuditLogger audit)
{
    private const string SystemRole = "system";

    [McpServerTool(Name = "user_lookup")]
    [Description("""
        Find users by id, email address, or name. Accepts a full uuid, or a case-insensitive
        substring of an email or name. If exactly one user matches, the full detail is
        included so no second call is needed.
        """)]
    public async Task<string> UserLookupAsync(
        [Description("A user id (uuid), or part of an email address or name.")] string query,
        [Description("How many matches to return. 1-50, default 20.")] int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ModelContextProtocol.McpException("Provide a user id, email or name to search for.");

        limit = McpPaging.Clamp(limit, 50);
        await audit.WriteAsync("user_lookup", new { query, limit }, McpAuditOutcomes.Ok, ct: ct);

        var users = db.Users.Where(u => u.Role != SystemRole);

        if (Guid.TryParse(query.Trim(), out var id))
        {
            users = users.Where(u => u.Id == id);
        }
        else
        {
            var term = $"%{query.Trim()}%";
            // Search names and every identity email, not just the email provider —
            // Apple and Google users have no email identity row of their own.
            users = users.Where(u =>
                (u.Name != null && EF.Functions.ILike(u.Name, term))
                || db.UserIdentities.Any(i => i.UserId == u.Id && i.ProviderEmail != null && EF.Functions.ILike(i.ProviderEmail, term)));
        }

        var total = await users.CountAsync(ct);
        var items = await users
            .OrderByDescending(u => u.CreatedAt)
            .Take(limit)
            .Select(u => new
            {
                id = u.Id,
                name = u.Name,
                email = db.UserIdentities.Where(i => i.UserId == u.Id && i.ProviderEmail != null)
                    .Select(i => i.ProviderEmail).FirstOrDefault(),
                providers = db.UserIdentities.Where(i => i.UserId == u.Id).Select(i => i.Provider).ToList(),
                role = u.Role,
                status = u.Status,
                nativeLanguage = u.NativeLanguage,
                learningLanguage = u.LearningLanguage,
                createdAt = u.CreatedAt,
                lastLoginAt = u.LastLoginAt,
            })
            .ToListAsync(ct);

        object? detail = null;
        if (total == 1 && items.Count == 1)
            detail = await BuildDetailAsync(items[0].id, ct);

        return McpJson.Serialize(new { asOf = DateTime.UtcNow, total, items, detail });
    }

    [McpServerTool(Name = "user_detail")]
    [Description("""
        The full admin view of one user: sign-in identities, languages, role and status, AI
        allowance, sessions and devices, push tokens (token values are never returned),
        content counts, chat counts and activity summary.
        """)]
    public async Task<string> UserDetailAsync(
        [Description("The user id.")] Guid userId,
        CancellationToken ct = default)
    {
        await audit.WriteAsync("user_detail", new { userId }, McpAuditOutcomes.Ok, targetUserId: userId, ct: ct);

        var detail = await BuildDetailAsync(userId, ct)
            ?? throw new ModelContextProtocol.McpException("No user found with that id.");

        return McpJson.Serialize(new { asOf = DateTime.UtcNow, user = detail });
    }

    [McpServerTool(Name = "users_list")]
    [Description("""
        A filterable, paged list of users for segmentation: by role, status, learning or
        native language family, creation date, recent activity, and whether they have a push
        token. Use it to answer "who is learning Spanish and was active this month".
        """)]
    public async Task<string> UsersListAsync(
        [Description("Filter by role: 'user' or 'admin'.")] string? role = null,
        [Description("Filter by status, e.g. 'active' or 'suspended'.")] string? status = null,
        [Description("Learning language, e.g. 'es'. Matched by language family.")] string? learningLanguage = null,
        [Description("Native language, e.g. 'zh-TW'. Matched by language family.")] string? nativeLanguage = null,
        [Description("Only users created on or after this date (YYYY-MM-DD).")] string? createdSince = null,
        [Description("Only users active within this many days.")] int? activeWithinDays = null,
        [Description("Filter on having at least one push token.")] bool? hasPushToken = null,
        [Description("Sort field: 'createdAt' (default), 'lastLoginAt' or 'name'.")] string? sort = null,
        [Description("Sort descending. Default true.")] bool desc = true,
        [Description("How many users. 1-100, default 50.")] int limit = 50,
        [Description("Rows to skip for paging.")] int offset = 0,
        CancellationToken ct = default)
    {
        limit = McpPaging.Clamp(limit);
        offset = McpPaging.ClampOffset(offset);

        var query = db.Users.Where(u => u.DeletedAt == null && u.Role != SystemRole);

        if (!string.IsNullOrWhiteSpace(role)) query = query.Where(u => u.Role == role);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(u => u.Status == status);

        if (!string.IsNullOrWhiteSpace(createdSince))
        {
            if (!DateOnly.TryParse(createdSince, out var since))
                throw new ModelContextProtocol.McpException("createdSince must be a date like 2026-01-31.");
            query = query.Where(u => u.CreatedAt >= since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        }

        if (activeWithinDays is > 0)
        {
            var sinceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-activeWithinDays.Value));
            query = query.Where(u => db.UserActivityDaily.Any(a => a.UserId == u.Id && a.Date >= sinceDate));
        }

        if (hasPushToken is not null)
        {
            query = hasPushToken.Value
                ? query.Where(u => db.UserPushTokens.Any(t => t.UserId == u.Id))
                : query.Where(u => !db.UserPushTokens.Any(t => t.UserId == u.Id));
        }

        var learningFamily = McpLanguageGrouping.ToFamilyKey(learningLanguage);
        if (learningFamily is not null)
            query = query.Where(u => u.LearningLanguage != null && EF.Functions.ILike(u.LearningLanguage, learningFamily + "%"));

        var nativeFamily = McpLanguageGrouping.ToFamilyKey(nativeLanguage);
        if (nativeFamily is not null)
            query = query.Where(u => u.NativeLanguage != null && EF.Functions.ILike(u.NativeLanguage, nativeFamily + "%"));

        query = (sort?.Trim().ToLowerInvariant()) switch
        {
            "lastloginat" => desc ? query.OrderByDescending(u => u.LastLoginAt) : query.OrderBy(u => u.LastLoginAt),
            "name" => desc ? query.OrderByDescending(u => u.Name) : query.OrderBy(u => u.Name),
            _ => desc ? query.OrderByDescending(u => u.CreatedAt) : query.OrderBy(u => u.CreatedAt),
        };

        var total = await query.CountAsync(ct);

        var items = await query
            .Skip(offset).Take(limit)
            .Select(u => new
            {
                id = u.Id,
                name = u.Name,
                email = db.UserIdentities.Where(i => i.UserId == u.Id && i.ProviderEmail != null)
                    .Select(i => i.ProviderEmail).FirstOrDefault(),
                role = u.Role,
                status = u.Status,
                nativeLanguage = u.NativeLanguage,
                learningLanguage = u.LearningLanguage,
                createdAt = u.CreatedAt,
                lastLoginAt = u.LastLoginAt,
            })
            .ToListAsync(ct);

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            total,
            returned = items.Count,
            offset,
            filters = new { role, status, learningLanguage = learningFamily, nativeLanguage = nativeFamily, createdSince, activeWithinDays, hasPushToken },
            items,
        });
    }

    [McpServerTool(Name = "audit_log_list")]
    [Description("""
        Recent admin actions taken through this server: which tool ran, with what arguments,
        by whom, and what happened. Use to review what changes have been made.
        """)]
    public async Task<string> AuditLogListAsync(
        [Description("Time window, e.g. '30d'. Default '30d'.")] string? period = null,
        [Description("Filter to one tool name.")] string? tool = null,
        [Description("Filter to actions affecting this user.")] Guid? targetUserId = null,
        [Description("How many entries. 1-100, default 50.")] int limit = 50,
        [Description("Rows to skip for paging.")] int offset = 0,
        CancellationToken ct = default)
    {
        var p = McpPeriod.Parse(period);
        limit = McpPaging.Clamp(limit);
        offset = McpPaging.ClampOffset(offset);

        var query = db.McpAuditLogs.Where(a => a.CreatedAt >= p.FromUtc && a.CreatedAt < p.ToUtc);
        if (!string.IsNullOrWhiteSpace(tool)) query = query.Where(a => a.Tool == tool);
        if (targetUserId is not null) query = query.Where(a => a.TargetUserId == targetUserId);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip(offset).Take(limit)
            .Select(a => new
            {
                a.Id,
                createdAt = a.CreatedAt,
                adminUserId = a.AdminUserId,
                adminName = db.Users.Where(u => u.Id == a.AdminUserId).Select(u => u.Name).FirstOrDefault(),
                clientId = a.ClientId,
                tool = a.Tool,
                args = a.ArgsJson,
                targetUserId = a.TargetUserId,
                targetId = a.TargetId,
                outcome = a.Outcome,
                result = a.ResultSummary,
                durationMs = a.DurationMs,
            })
            .ToListAsync(ct);

        return McpJson.Serialize(new { asOf = DateTime.UtcNow, total, items });
    }

    // ── shared detail projection ──────────────────────────────────────────────
    private async Task<object?> BuildDetailAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;

        var now = DateTime.UtcNow;
        var since7 = now.AddDays(-7);
        var since30 = DateOnly.FromDateTime(now.AddDays(-30));

        var identities = await db.UserIdentities
            .Where(i => i.UserId == userId)
            .Select(i => new { provider = i.Provider, email = i.ProviderEmail, createdAt = i.CreatedAt })
            .ToListAsync(ct);

        var sessions = await db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .Select(s => new { s.DeviceName, s.CreatedAt, s.ExpiresAt })
            .ToListAsync(ct);

        var pushTokens = await db.UserPushTokens
            .Where(t => t.UserId == userId)
            .Select(t => new { platform = t.Platform, sandbox = t.IsSandbox, supportsCalls = t.SupportsCalls, lastSeenAt = t.LastSeenAt })
            .ToListAsync(ct);

        var uploads = await db.UserVideos.CountAsync(v => v.UserId == userId && !v.IsAiGenerated, ct);
        var aiAudio = await db.UserVideos.CountAsync(v => v.UserId == userId && v.IsAiGenerated, ct);
        var savedVideos = await db.UserSavedVideos.CountAsync(s => s.UserId == userId, ct);
        var savedCues = await db.UserSavedCues.CountAsync(s => s.UserId == userId, ct);

        var threads = await db.ChatThreadMemberships.CountAsync(m => m.UserId == userId && m.DeletedAt == null, ct);
        var messages7 = await db.ChatMessages.CountAsync(m => m.SenderUserId == userId && m.CreatedAt >= since7, ct);
        var blocks = await db.ChatBlocks.CountAsync(b => b.BlockerUserId == userId, ct);

        var activeDays30 = await db.UserActivityDaily.CountAsync(a => a.UserId == userId && a.Date >= since30, ct);
        var firstSeen = await db.UserActivityDaily.Where(a => a.UserId == userId).MinAsync(a => (DateOnly?)a.Date, ct);
        var lastSeen = await db.UserActivityDaily.Where(a => a.UserId == userId).MaxAsync(a => (DateOnly?)a.Date, ct);

        return new
        {
            id = user.Id,
            name = user.Name,
            role = user.Role,
            status = user.Status,
            nativeLanguage = user.NativeLanguage,
            learningLanguage = user.LearningLanguage,
            translationLanguage = user.TranslationLanguage,
            aiAudioDailyLimit = user.AiAudioDailyLimit,
            chatNotificationsEnabled = user.ChatNotificationsEnabled,
            alwaysOnline = user.AlwaysOnline,
            createdAt = user.CreatedAt,
            lastLoginAt = user.LastLoginAt,
            identities,
            sessions = new { active = sessions.Count, devices = sessions.Select(s => s.DeviceName).Where(d => d != null) },
            pushTokens,
            content = new { uploads, aiAudio, savedVideos, savedCues },
            chat = new { threads, messagesLast7d = messages7, blocks },
            activity = new { activeDaysLast30 = activeDays30, firstSeen, lastSeen },
        };
    }
}

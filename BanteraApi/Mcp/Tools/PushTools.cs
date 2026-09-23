using System.ComponentModel;
using BanteraApi.Chat;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace BanteraApi.Mcp.Tools;

/// <summary>Push-notification reach and segment sizing (read-only).</summary>
[McpServerToolType]
public sealed class PushTools(AppDbContext db, IOptions<ApnsSettings> apns)
{
    [McpServerTool(Name = "push_token_stats")]
    [Description("""
        How many users can actually be reached by push: registered device tokens, platform
        split, sandbox versus production, call-capable share, how fresh the tokens are, and
        how many users have turned notifications off.
        """)]
    public async Task<string> PushTokenStatsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var tokens = await db.UserPushTokens.CountAsync(ct);
        var users = await db.UserPushTokens.Select(t => t.UserId).Distinct().CountAsync(ct);

        var byPlatform = await db.UserPushTokens
            .GroupBy(t => t.Platform)
            .Select(g => new { g.Key, N = g.Count() })
            .ToListAsync(ct);

        var sandbox = await db.UserPushTokens.CountAsync(t => t.IsSandbox, ct);
        var calls = await db.UserPushTokens.CountAsync(t => t.SupportsCalls, ct);

        var fresh7 = await db.UserPushTokens.CountAsync(t => t.LastSeenAt >= now.AddDays(-7), ct);
        var fresh30 = await db.UserPushTokens.CountAsync(t => t.LastSeenAt >= now.AddDays(-30), ct);

        var notificationsOff = await db.Users
            .CountAsync(u => u.DeletedAt == null && !u.ChatNotificationsEnabled, ct);

        var reachable = await db.Users
            .CountAsync(u => u.DeletedAt == null
                          && u.Status == "active"
                          && u.ChatNotificationsEnabled
                          && db.UserPushTokens.Any(t => t.UserId == u.Id), ct);

        return McpJson.Serialize(new
        {
            asOf = now,
            apnsConfigured = apns.Value.HasConfiguration,
            tokens,
            usersWithToken = users,
            byPlatform = byPlatform.ToDictionary(x => x.Key, x => x.N),
            sandbox,
            production = tokens - sandbox,
            supportsCalls = calls,
            freshness = new { last7d = fresh7, last30d = fresh30, older = tokens - fresh30 },
            usersWithNotificationsDisabled = notificationsOff,
            usersReachable = reachable,
            caveats = apns.Value.HasConfiguration
                ? []
                : new[] { "Push is not configured on this server, so notifications would not actually be delivered." },
        });
    }

    [McpServerTool(Name = "push_segment_preview")]
    [Description("""
        Counts how many users a segmented push would reach, without sending anything. Takes
        the same filters as send_push_to_segment. Always run this before sending.
        """)]
    public async Task<string> PushSegmentPreviewAsync(
        [Description("Learning language, e.g. 'es' or 'es-MX'. Matched by language family.")] string? learningLanguage = null,
        [Description("Native language, e.g. 'zh-TW'. Matched by language family.")] string? nativeLanguage = null,
        [Description("Only users active within this many days.")] int? activeWithinDays = null,
        [Description("Account status to include. Default 'active'.")] string? status = null,
        [Description("Only users with at least one push token and notifications enabled. Default true.")] bool requirePushToken = true,
        CancellationToken ct = default)
    {
        var segment = await BuildSegmentAsync(db, learningLanguage, nativeLanguage, activeWithinDays, status, requirePushToken, ct);

        var sample = segment.Users.Take(10).Select(u => new { userId = u.Id, name = u.Name }).ToList();

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            filters = new
            {
                learningLanguage = segment.LearningFamily,
                nativeLanguage = segment.NativeFamily,
                activeWithinDays,
                status = segment.Status,
                requirePushToken,
            },
            matchedUsers = segment.Users.Count,
            reachableUsers = segment.ReachableCount,
            tokens = segment.TokenCount,
            sample,
        });
    }

    internal sealed record SegmentResult(
        List<User> Users,
        int ReachableCount,
        int TokenCount,
        string? LearningFamily,
        string? NativeFamily,
        string Status);

    /// <summary>
    /// Shared by the preview tool and the send tool so a preview always describes exactly
    /// what a send would do.
    /// </summary>
    internal static async Task<SegmentResult> BuildSegmentAsync(
        AppDbContext db,
        string? learningLanguage,
        string? nativeLanguage,
        int? activeWithinDays,
        string? status,
        bool requirePushToken,
        CancellationToken ct)
    {
        var effectiveStatus = string.IsNullOrWhiteSpace(status) ? "active" : status.Trim().ToLowerInvariant();

        var query = db.Users.Where(u => u.DeletedAt == null && u.Role != "system" && u.Status == effectiveStatus);

        if (requirePushToken)
        {
            query = query.Where(u => u.ChatNotificationsEnabled && db.UserPushTokens.Any(t => t.UserId == u.Id));
        }

        if (activeWithinDays is > 0)
        {
            var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-activeWithinDays.Value));
            query = query.Where(u => db.UserActivityDaily.Any(a => a.UserId == u.Id && a.Date >= since));
        }

        var learningFamily = McpLanguageGrouping.ToFamilyKey(learningLanguage);
        var nativeFamily = McpLanguageGrouping.ToFamilyKey(nativeLanguage);

        // Narrow in SQL on the family prefix, then confirm exactly in memory — the stored
        // codes are free text with inconsistent casing.
        if (learningFamily is not null)
            query = query.Where(u => u.LearningLanguage != null && EF.Functions.ILike(u.LearningLanguage, learningFamily + "%"));

        if (nativeFamily is not null)
            query = query.Where(u => u.NativeLanguage != null && EF.Functions.ILike(u.NativeLanguage, nativeFamily + "%"));

        var candidates = await query
            .OrderByDescending(u => u.LastLoginAt)
            .Take(5000)
            .ToListAsync(ct);

        if (learningFamily is not null)
            candidates = [.. candidates.Where(u => McpLanguageGrouping.IsInFamily(u.LearningLanguage, learningFamily))];

        if (nativeFamily is not null)
            candidates = [.. candidates.Where(u => McpLanguageGrouping.IsInFamily(u.NativeLanguage, nativeFamily))];

        var ids = candidates.Select(u => u.Id).ToList();

        var tokenCount = await db.UserPushTokens.CountAsync(t => ids.Contains(t.UserId), ct);
        var reachable = await db.Users
            .CountAsync(u => ids.Contains(u.Id)
                          && u.ChatNotificationsEnabled
                          && db.UserPushTokens.Any(t => t.UserId == u.Id), ct);

        return new SegmentResult(candidates, reachable, tokenCount, learningFamily, nativeFamily, effectiveStatus);
    }
}

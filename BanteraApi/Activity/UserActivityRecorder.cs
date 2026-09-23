using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BanteraApi.Activity;

/// <summary>
/// Records that a user was active on a given UTC day, backing exact DAU/WAU/MAU.
///
/// A memory-cache guard keeps this to at most one write per user per hour (and therefore
/// at most 24 per user per day) instead of one per request. The upsert is safe across
/// instances because it relies on ON CONFLICT rather than a read-then-write.
/// </summary>
public class UserActivityRecorder(IMemoryCache cache, ILogger<UserActivityRecorder> logger)
{
    private static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(60);

    public async Task TouchAsync(Guid userId, AppDbContext db, VisitorGeo? geo = null, CancellationToken ct = default)
    {
        geo ??= VisitorGeo.None;
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var key = $"activity:{userId:N}:{today:yyyyMMdd}";

        if (cache.TryGetValue(key, out _))
            return;

        // Expire at the earlier of the next UTC midnight or one hour from now, so the
        // first request of a new day always writes.
        var untilMidnight = today.AddDays(1).ToDateTime(TimeOnly.MinValue) - now;
        var ttl = untilMidnight < MaxWindow ? untilMidnight : MaxWindow;
        if (ttl <= TimeSpan.Zero) ttl = TimeSpan.FromMinutes(1);

        // Set before writing so a transient DB failure does not retry on every request.
        cache.Set(key, true, ttl);

        try
        {
            // Location keeps the latest non-null value for the day, so a request that
            // arrives without Cloudflare headers never erases a known location.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO user_activity_daily
                    ("UserId","Date","FirstSeenAt","LastSeenAt","TouchCount","MessagesSent","Source",
                     "CountryCode","Region","City")
                VALUES ({userId}, {today}, {now}, {now}, 1, 0, 'live',
                        {geo.CountryCode}, {geo.Region}, {geo.City})
                ON CONFLICT ("UserId","Date") DO UPDATE
                    SET "LastSeenAt"  = EXCLUDED."LastSeenAt",
                        "TouchCount"  = user_activity_daily."TouchCount" + 1,
                        "CountryCode" = COALESCE(EXCLUDED."CountryCode", user_activity_daily."CountryCode"),
                        "Region"      = COALESCE(EXCLUDED."Region", user_activity_daily."Region"),
                        "City"        = COALESCE(EXCLUDED."City", user_activity_daily."City")
                """, ct);
        }
        catch (Exception ex)
        {
            cache.Remove(key);
            logger.LogWarning(ex, "[Activity] Failed to record activity for user {UserId}", userId);
        }
    }

    /// <summary>
    /// Increments the durable per-day message counter. Chat messages themselves are deleted
    /// after seven days, so this is the only long-term record of messaging volume.
    /// </summary>
    public async Task BumpMessagesSentAsync(Guid userId, AppDbContext db, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO user_activity_daily
                    ("UserId","Date","FirstSeenAt","LastSeenAt","TouchCount","MessagesSent","Source")
                VALUES ({userId}, {today}, {now}, {now}, 1, 1, 'live')
                ON CONFLICT ("UserId","Date") DO UPDATE
                    SET "LastSeenAt" = EXCLUDED."LastSeenAt",
                        "MessagesSent" = user_activity_daily."MessagesSent" + 1
                """, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Activity] Failed to record message for user {UserId}", userId);
        }
    }
}

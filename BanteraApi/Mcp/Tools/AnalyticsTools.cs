using System.ComponentModel;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace BanteraApi.Mcp.Tools;

/// <summary>
/// User-population analytics: registrations, active users, retention and language mix.
/// </summary>
[McpServerToolType]
public sealed class AnalyticsTools(AppDbContext db, Admin.AdminAnalyticsService analytics)
{
    [McpServerTool(Name = "location_breakdown")]
    [Description("""
        Where users are: user counts per country and the top cities, based on each user's
        most recent known location from Cloudflare's visitor-location headers. Answers "which
        countries are our users in". Location is only recorded from the day geo tracking went
        live, so users who have not opened the app since then are counted as unknown.
        """)]
    public async Task<string> LocationBreakdownAsync(CancellationToken ct = default)
    {
        var (countries, cities, withoutLocation) = await analytics.GetLocationsAsync(ct);
        var located = countries.Sum(c => c.Users);

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            usersWithLocation = located,
            usersWithoutLocation = withoutLocation,
            countries = countries.Select(c => new
            {
                c.Code,
                c.Flag,
                c.Users,
                pct = located == 0 ? 0 : Math.Round(c.Users * 100.0 / located, 1),
            }),
            topCities = cities,
            caveats = new[]
            {
                "Location comes from the IP address Cloudflare saw, so VPN users appear wherever their VPN exits.",
                "City and region need Cloudflare's 'Add visitor location headers' managed transform; without it only the country is recorded.",
            },
        });
    }

    private const string SystemRole = "system";

    private static readonly string[] RegistrationCaveat =
        ["Counts only accounts that still exist; deleted accounts are removed entirely and cannot be counted."];

    [McpServerTool(Name = "users_overview")]
    [Description("""
        Headline user metrics as of now: total users, breakdown by role, status and sign-in
        provider, how many have set their languages, how many can receive push notifications,
        registrations in the last 7 and 30 days, and the current DAU, WAU and MAU.
        """)]
    public async Task<string> UsersOverviewAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        var users = db.Users.Where(u => u.DeletedAt == null && u.Role != SystemRole);

        var total = await users.CountAsync(ct);
        var byRole = await users.GroupBy(u => u.Role).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var byStatus = await users.GroupBy(u => u.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);

        var byProvider = await db.UserIdentities
            .Where(i => db.Users.Any(u => u.Id == i.UserId && u.DeletedAt == null && u.Role != SystemRole))
            .GroupBy(i => i.Provider)
            .Select(g => new { g.Key, N = g.Count() })
            .ToListAsync(ct);

        var withLearning = await users.CountAsync(u => u.LearningLanguage != null && u.LearningLanguage != "", ct);
        var withNative = await users.CountAsync(u => u.NativeLanguage != null && u.NativeLanguage != "", ct);

        var withPush = await db.UserPushTokens
            .Select(t => t.UserId).Distinct().CountAsync(ct);

        var reg7 = await users.CountAsync(u => u.CreatedAt >= now.AddDays(-7), ct);
        var reg30 = await users.CountAsync(u => u.CreatedAt >= now.AddDays(-30), ct);

        var dau = await CountActiveSinceAsync(today, 1, ct);
        var wau = await CountActiveSinceAsync(today, 7, ct);
        var mau = await CountActiveSinceAsync(today, 30, ct);

        return McpJson.Serialize(new
        {
            asOf = now,
            totalUsers = total,
            byRole = byRole.ToDictionary(x => x.Key, x => x.N),
            byStatus = byStatus.ToDictionary(x => x.Key, x => x.N),
            byProvider = byProvider.ToDictionary(x => x.Key, x => x.N),
            languagesSet = new
            {
                learning = withLearning,
                native = withNative,
                learningPct = Pct(withLearning, total),
                nativePct = Pct(withNative, total),
            },
            usersWithPushToken = withPush,
            registrations = new { last7d = reg7, last30d = reg30 },
            active = new { dau, wau, mau },
            caveats = RegistrationCaveat,
        });
    }

    [McpServerTool(Name = "registrations_timeseries")]
    [Description("""
        Account creations over time as a gap-free series (buckets with no sign-ups are
        returned as zero). Use for questions like "how many people registered this week" or
        "show me sign-ups per month this year".
        """)]
    public async Task<string> RegistrationsTimeseriesAsync(
        [Description("Time window: '7d', '12w', '6m', 'today', or '2026-01-01..2026-02-01'. Default '90d'.")]
        string? period = null,
        [Description("Bucket size: 'day', 'week', 'month', or 'auto' (default) which picks one from the window length.")]
        string? bucket = null,
        [Description("Split each bucket by sign-in provider (apple/google/email) instead of a single total.")]
        bool byProvider = false,
        CancellationToken ct = default)
    {
        var p = McpPeriod.Parse(period, "90d");
        var unit = McpBuckets.Validate(bucket, p);
        var interval = McpBuckets.Interval(unit);

        if (byProvider)
        {
            var rows = await db.Database.SqlQueryRaw<McpSql.ProviderBucketRow>($$"""
                SELECT date_trunc('{{unit}}', u."CreatedAt" AT TIME ZONE 'UTC') AS "Bucket",
                       i."Provider" AS "Provider",
                       COUNT(*) AS "Count"
                FROM users u
                JOIN user_identities i ON i."UserId" = u."Id"
                WHERE u."DeletedAt" IS NULL AND u."Role" <> 'system'
                  AND u."CreatedAt" >= {0} AND u."CreatedAt" < {1}
                GROUP BY 1, 2
                """, p.FromUtc, p.ToUtc).ToListAsync(ct);

            var buckets = await BuildBucketsAsync(unit, interval, p, ct);
            var byBucket = rows.GroupBy(r => r.Bucket).ToDictionary(g => g.Key, g => g.ToList());

            var series = buckets.Select(b => new
            {
                bucket = DateOnly.FromDateTime(b),
                apple = Provider(byBucket, b, "apple"),
                google = Provider(byBucket, b, "google"),
                email = Provider(byBucket, b, "email"),
                total = byBucket.TryGetValue(b, out var l) ? l.Sum(x => x.Count) : 0,
            }).ToList();

            return McpJson.Serialize(new
            {
                asOf = DateTime.UtcNow,
                period = new { from = p.FromUtc, to = p.ToUtc, label = p.Label },
                bucket = unit,
                total = series.Sum(s => s.total),
                series,
                caveats = RegistrationCaveat,
            });
        }

        var counts = await db.Database.SqlQueryRaw<McpSql.BucketCountRow>($$"""
            SELECT date_trunc('{{unit}}', "CreatedAt" AT TIME ZONE 'UTC') AS "Bucket", COUNT(*) AS "Count"
            FROM users
            WHERE "DeletedAt" IS NULL AND "Role" <> 'system'
              AND "CreatedAt" >= {0} AND "CreatedAt" < {1}
            GROUP BY 1
            """, p.FromUtc, p.ToUtc).ToListAsync(ct);

        var allBuckets = await BuildBucketsAsync(unit, interval, p, ct);
        var lookup = counts.ToDictionary(c => c.Bucket, c => c.Count);

        var filled = allBuckets
            .Select(b => new { bucket = DateOnly.FromDateTime(b), count = lookup.GetValueOrDefault(b, 0L) })
            .ToList();

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            period = new { from = p.FromUtc, to = p.ToUtc, label = p.Label },
            bucket = unit,
            total = filled.Sum(f => f.count),
            series = filled,
            caveats = RegistrationCaveat,
        });
    }

    [McpServerTool(Name = "active_users")]
    [Description("""
        Daily active users for each day in the period, with rolling 7-day (WAU) and 30-day
        (MAU) unique-user counts, plus the current values. Exact from the day activity
        tracking went live; earlier days are reconstructed and marked approximate.
        """)]
    public async Task<string> ActiveUsersAsync(
        [Description("Time window, e.g. '30d', '12w', '2026-01-01..2026-03-01'. Default '30d'. At most 400 days.")]
        string? period = null,
        [Description("Exclude admin accounts, so internal use does not inflate the numbers.")]
        bool excludeAdmins = false,
        CancellationToken ct = default)
    {
        var p = McpPeriod.Parse(period, "30d");
        if (p.TotalDays > 400)
            throw new ModelContextProtocol.McpException("Period too long: active_users supports at most 400 days.");

        var from = DateOnly.FromDateTime(p.FromUtc);
        var to = DateOnly.FromDateTime(p.ToUtc.AddDays(-1));

        var adminFilter = excludeAdmins
            ? """AND a."UserId" NOT IN (SELECT "Id" FROM users WHERE "Role" = 'admin')"""
            : "";

        var rows = await db.Database.SqlQueryRaw<McpSql.ActiveRow>($$"""
            SELECT d::date AS "Date",
              (SELECT COUNT(DISTINCT a."UserId") FROM user_activity_daily a
                WHERE a."Date" = d::date {{adminFilter}}) AS "Dau",
              (SELECT COUNT(DISTINCT a."UserId") FROM user_activity_daily a
                WHERE a."Date" > d::date - 7 AND a."Date" <= d::date {{adminFilter}}) AS "Wau",
              (SELECT COUNT(DISTINCT a."UserId") FROM user_activity_daily a
                WHERE a."Date" > d::date - 30 AND a."Date" <= d::date {{adminFilter}}) AS "Mau"
            FROM generate_series({0}::date, {1}::date, interval '1 day') AS d
            ORDER BY 1
            """, from, to).ToListAsync(ct);

        var liveSince = await db.UserActivityDaily
            .Where(a => a.Source == "live")
            .MinAsync(a => (DateOnly?)a.Date, ct);

        var series = rows.Select(r => new
        {
            date = r.Date,
            dau = r.Dau,
            wau = r.Wau,
            mau = r.Mau,
            approximate = liveSince is null || r.Date < liveSince,
        }).ToList();

        var last = series.LastOrDefault();

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            period = new { from, to, label = p.Label },
            liveSince,
            current = last is null ? null : new { dau = last.dau, wau = last.wau, mau = last.mau },
            series,
            caveats = new[]
            {
                liveSince is null
                    ? "Activity tracking has not recorded any live days yet; every figure here is reconstructed from historical timestamps."
                    : $"Days before {liveSince:yyyy-MM-dd} are reconstructed from content, chat, session and login timestamps and undercount users who only browsed.",
            },
        });
    }

    [McpServerTool(Name = "retention_cohorts")]
    [Description("""
        Weekly retention: for each signup week, how many of those users were active in each
        following week, as counts and percentages. Only meaningful for cohorts after activity
        tracking went live.
        """)]
    public async Task<string> RetentionCohortsAsync(
        [Description("How many signup weeks to include, most recent last. 1-26, default 8.")]
        int weeks = 8,
        [Description("How many weeks after signup to follow each cohort. 1-26, default 8.")]
        int maxOffset = 8,
        CancellationToken ct = default)
    {
        weeks = Math.Clamp(weeks, 1, 26);
        maxOffset = Math.Clamp(maxOffset, 1, 26);

        var from = DateTime.UtcNow.Date.AddDays(-7 * (weeks + maxOffset));

        var sizes = await db.Database.SqlQueryRaw<McpSql.CohortSizeRow>("""
            SELECT date_trunc('week', "CreatedAt" AT TIME ZONE 'UTC')::date AS "CohortWeek", COUNT(*) AS "Size"
            FROM users
            WHERE "DeletedAt" IS NULL AND "Role" <> 'system' AND "CreatedAt" >= {0}
            GROUP BY 1 ORDER BY 1
            """, from).ToListAsync(ct);

        var retention = await db.Database.SqlQueryRaw<McpSql.CohortRow>("""
            WITH cohorts AS (
              SELECT "Id" AS uid, date_trunc('week', "CreatedAt" AT TIME ZONE 'UTC')::date AS cw
              FROM users
              WHERE "DeletedAt" IS NULL AND "Role" <> 'system' AND "CreatedAt" >= {0}
            ), act AS (
              SELECT DISTINCT "UserId" AS uid, date_trunc('week', "Date"::timestamp)::date AS aw
              FROM user_activity_daily WHERE "Date" >= {0}::date
            )
            SELECT c.cw AS "CohortWeek",
                   ((a.aw - c.cw) / 7)::int AS "WeekOffset",
                   COUNT(DISTINCT a.uid) AS "Retained"
            FROM cohorts c
            JOIN act a ON a.uid = c.uid AND a.aw >= c.cw
            GROUP BY 1, 2 ORDER BY 1, 2
            """, from).ToListAsync(ct);

        var byCohort = retention.GroupBy(r => r.CohortWeek).ToDictionary(g => g.Key, g => g.ToDictionary(x => x.WeekOffset, x => x.Retained));

        var cohorts = sizes
            .OrderBy(s => s.CohortWeek)
            .TakeLast(weeks)
            .Select(s =>
            {
                var offsets = byCohort.GetValueOrDefault(s.CohortWeek) ?? [];
                var retained = Enumerable.Range(0, maxOffset + 1).Select(i => offsets.GetValueOrDefault(i, 0L)).ToList();
                return new
                {
                    week = s.CohortWeek,
                    size = s.Size,
                    retained,
                    pct = retained.Select(r => Pct((int)r, (int)s.Size)).ToList(),
                };
            })
            .ToList();

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            cohorts,
            caveats = new[]
            {
                "Retention is based on activity tracking, so cohorts from before it went live look artificially low.",
                "Users who deleted their account are not in any cohort.",
            },
        });
    }

    [McpServerTool(Name = "language_breakdown")]
    [Description("""
        How many users have each language set, for learning, native or translation. Codes are
        grouped into language families by default (en-US and en-GB both count as 'en') with
        the individual variants listed inside each group. Answers "how many users per native
        language" and "which languages are people learning".
        """)]
    public async Task<string> LanguageBreakdownAsync(
        [Description("Which language field: 'learning', 'native' or 'translation'.")]
        string kind,
        [Description("'family' (default) groups regional variants together; 'exact' keeps each raw code separate.")]
        string? groupBy = null,
        [Description("Optionally restrict to users created in this window, e.g. '30d'. Omit for all users.")]
        string? period = null,
        CancellationToken ct = default)
    {
        var normalizedKind = (kind ?? "").Trim().ToLowerInvariant();
        if (normalizedKind is not ("learning" or "native" or "translation"))
            throw new ModelContextProtocol.McpException("kind must be 'learning', 'native' or 'translation'.");

        var byFamily = !string.Equals(groupBy, "exact", StringComparison.OrdinalIgnoreCase);

        var query = db.Users.Where(u => u.DeletedAt == null && u.Role != SystemRole);
        McpPeriod? p = null;
        if (!string.IsNullOrWhiteSpace(period))
        {
            p = McpPeriod.Parse(period);
            query = query.Where(u => u.CreatedAt >= p.FromUtc && u.CreatedAt < p.ToUtc);
        }

        var rows = normalizedKind switch
        {
            "learning" => await query.GroupBy(u => u.LearningLanguage)
                .Select(g => new McpSql.CodeCountRow(g.Key, g.Count())).ToListAsync(ct),
            "native" => await query.GroupBy(u => u.NativeLanguage)
                .Select(g => new McpSql.CodeCountRow(g.Key, g.Count())).ToListAsync(ct),
            _ => await query.GroupBy(u => u.TranslationLanguage)
                .Select(g => new McpSql.CodeCountRow(g.Key, g.Count())).ToListAsync(ct),
        };

        var totalUsers = rows.Sum(r => r.Count);
        var groups = McpLanguageGrouping.Group(rows.Select(r => (r.Code, r.Count)), byFamily);
        var unset = groups.FirstOrDefault(g => g.Key == McpLanguageGrouping.UnsetKey)?.Users ?? 0;

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            kind = normalizedKind,
            groupBy = byFamily ? "family" : "exact",
            period = p is null ? null : new { from = p.FromUtc, to = p.ToUtc, label = p.Label },
            totalUsers,
            unset,
            groups = groups.Where(g => g.Key != McpLanguageGrouping.UnsetKey),
        });
    }

    [McpServerTool(Name = "language_matrix")]
    [Description("""
        The most common native-language to learning-language pairs, showing what people speak
        versus what they are learning. Both sides are grouped into language families.
        """)]
    public async Task<string> LanguageMatrixAsync(
        [Description("How many pairs to return, most common first. 1-100, default 25.")]
        int limit = 25,
        CancellationToken ct = default)
    {
        limit = McpPaging.Clamp(limit);

        var rows = await db.Users
            .Where(u => u.DeletedAt == null && u.Role != SystemRole
                     && u.NativeLanguage != null && u.NativeLanguage != ""
                     && u.LearningLanguage != null && u.LearningLanguage != "")
            .GroupBy(u => new { u.NativeLanguage, u.LearningLanguage })
            .Select(g => new McpSql.PairCountRow(g.Key.NativeLanguage, g.Key.LearningLanguage, g.Count()))
            .ToListAsync(ct);

        var regrouped = rows
            .Select(r => new
            {
                Native = Chat.ChatLanguageResolver.Resolve(r.Native),
                Learning = Chat.ChatLanguageResolver.Resolve(r.Learning),
                r.Count,
            })
            .Where(r => r.Native is not null && r.Learning is not null)
            .GroupBy(r => new { N = r.Native!.MatchKey, L = r.Learning!.MatchKey })
            .Select(g => new
            {
                native = g.Key.N,
                nativeName = g.First().Native!.DisplayName,
                learning = g.Key.L,
                learningName = g.First().Learning!.DisplayName,
                users = g.Sum(x => x.Count),
            })
            .OrderByDescending(g => g.users)
            .ToList();

        var totalWithBoth = regrouped.Sum(r => r.users);

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            totalWithBoth,
            pairs = regrouped.Take(limit).Select(r => new
            {
                r.native,
                r.nativeName,
                r.learning,
                r.learningName,
                r.users,
                pct = Pct(r.users, totalWithBoth),
            }),
        });
    }

    [McpServerTool(Name = "top_users")]
    [Description("""
        The most active users in a period, ranked by one dimension: 'videos' (uploads),
        'ai_audio' (generated lessons), 'messages' (voice messages sent), 'saved' (items
        saved) or 'active_days'. Returns user ids with names and emails.
        """)]
    public async Task<string> TopUsersAsync(
        [Description("Ranking dimension: 'videos', 'ai_audio', 'messages', 'saved' or 'active_days'.")]
        string by = "videos",
        [Description("Time window, e.g. '30d'. Default '30d'.")]
        string? period = null,
        [Description("How many users to return. 1-100, default 20.")]
        int limit = 20,
        CancellationToken ct = default)
    {
        var p = McpPeriod.Parse(period);
        limit = McpPaging.Clamp(limit);
        var dimension = (by ?? "videos").Trim().ToLowerInvariant();

        List<McpSql.TopUserRow> ranked = dimension switch
        {
            "videos" => await db.UserVideos
                .Where(v => !v.IsAiGenerated && v.CreatedAt >= p.FromUtc && v.CreatedAt < p.ToUtc)
                .GroupBy(v => v.UserId)
                .Select(g => new McpSql.TopUserRow(g.Key, g.Count()))
                .OrderByDescending(r => r.Count).Take(limit).ToListAsync(ct),

            "ai_audio" => await db.UserVideos
                .Where(v => v.IsAiGenerated && v.CreatedAt >= p.FromUtc && v.CreatedAt < p.ToUtc)
                .GroupBy(v => v.UserId)
                .Select(g => new McpSql.TopUserRow(g.Key, g.Count()))
                .OrderByDescending(r => r.Count).Take(limit).ToListAsync(ct),

            "messages" => await db.ChatMessages
                .Where(m => m.CreatedAt >= p.FromUtc && m.CreatedAt < p.ToUtc)
                .GroupBy(m => m.SenderUserId)
                .Select(g => new McpSql.TopUserRow(g.Key, g.Count()))
                .OrderByDescending(r => r.Count).Take(limit).ToListAsync(ct),

            "saved" => await db.UserSavedVideos
                .Where(s => s.SavedAt >= p.FromUtc && s.SavedAt < p.ToUtc)
                .GroupBy(s => s.UserId)
                .Select(g => new McpSql.TopUserRow(g.Key, g.Count()))
                .OrderByDescending(r => r.Count).Take(limit).ToListAsync(ct),

            "active_days" => await db.UserActivityDaily
                .Where(a => a.Date >= DateOnly.FromDateTime(p.FromUtc) && a.Date < DateOnly.FromDateTime(p.ToUtc))
                .GroupBy(a => a.UserId)
                .Select(g => new McpSql.TopUserRow(g.Key, g.Count()))
                .OrderByDescending(r => r.Count).Take(limit).ToListAsync(ct),

            _ => throw new ModelContextProtocol.McpException(
                "by must be 'videos', 'ai_audio', 'messages', 'saved' or 'active_days'."),
        };

        var ids = ranked.Select(r => r.UserId).ToList();
        var users = await db.Users
            .Where(u => ids.Contains(u.Id))
            .Select(u => new
            {
                u.Id,
                u.Name,
                u.LearningLanguage,
                Email = db.UserIdentities
                    .Where(i => i.UserId == u.Id && i.ProviderEmail != null)
                    .Select(i => i.ProviderEmail)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var byId = users.ToDictionary(u => u.Id);

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            by = dimension,
            period = new { from = p.FromUtc, to = p.ToUtc, label = p.Label },
            items = ranked.Select(r => new
            {
                userId = r.UserId,
                name = byId.GetValueOrDefault(r.UserId)?.Name,
                email = byId.GetValueOrDefault(r.UserId)?.Email,
                learningLanguage = byId.GetValueOrDefault(r.UserId)?.LearningLanguage,
                count = r.Count,
            }),
            caveats = dimension == "messages"
                ? new[] { "Voice messages are deleted 7 days after sending, so older activity is not counted." }
                : [],
        });
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private Task<int> CountActiveSinceAsync(DateOnly today, int days, CancellationToken ct)
        => db.UserActivityDaily
            .Where(a => a.Date > today.AddDays(-days) && a.Date <= today)
            .Select(a => a.UserId)
            .Distinct()
            .CountAsync(ct);

    private async Task<List<DateTime>> BuildBucketsAsync(string unit, string interval, McpPeriod p, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<BucketRow>($$"""
            SELECT g.b AS "Bucket"
            FROM generate_series(date_trunc('{{unit}}', {0}::timestamptz AT TIME ZONE 'UTC'), {1}::timestamptz AT TIME ZONE 'UTC', interval '{{interval}}') AS g(b)
            """, p.FromUtc, p.ToUtc.AddSeconds(-1)).ToListAsync(ct);

        return [.. rows.Select(r => r.Bucket)];
    }

    private sealed record BucketRow(DateTime Bucket);

    private static long Provider(Dictionary<DateTime, List<McpSql.ProviderBucketRow>> byBucket, DateTime b, string provider)
        => byBucket.TryGetValue(b, out var list)
            ? list.Where(x => x.Provider == provider).Sum(x => x.Count)
            : 0;

    private static double Pct(int part, int total)
        => total == 0 ? 0 : Math.Round(part * 100.0 / total, 1);
}

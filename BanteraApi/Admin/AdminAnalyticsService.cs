using BanteraApi.Database;
using BanteraApi.Mcp;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Admin;

public sealed record AnalyticsKpis(
    int TotalUsers,
    int NewUsers,
    int NewUsersPreviousPeriod,
    long Dau,
    long Wau,
    long Mau,
    int TotalContent,
    int Uploads,
    int AiAudio,
    int AiJobs,
    double AiJobSuccessRatePct,
    int UsersWithPushToken);

public sealed record CountPoint(DateOnly Date, long Count);
public sealed record ActivePoint(DateOnly Date, long Dau, long Wau, long Mau, bool Approximate);
public sealed record ContentPoint(DateOnly Date, long Uploads, long AiAudio);

public sealed record LanguageBreakdown(
    IReadOnlyList<LanguageGroup> ByAccent,
    IReadOnlyList<LanguageGroup> Combined,
    int Unset);

public sealed record CountryRow(string Code, string Flag, long Users);
public sealed record CityRow(string City, string? Region, string CountryCode, string Flag, long Users);
public sealed record ProviderRow(string Provider, int Users);

public sealed record LanguagePairRow(
    string Native, string NativeName, string NativeFlag,
    string Learning, string LearningName, string LearningFlag,
    int Users);

public sealed record AdminAnalytics(
    DateTime AsOf,
    int RangeDays,
    string Bucket,
    DateOnly? LiveTrackingSince,
    AnalyticsKpis Kpis,
    IReadOnlyList<CountPoint> Signups,
    IReadOnlyList<ActivePoint> ActiveUsers,
    IReadOnlyList<ContentPoint> Content,
    LanguageBreakdown NativeLanguages,
    LanguageBreakdown LearningLanguages,
    IReadOnlyList<CountryRow> Countries,
    long UsersWithoutLocation,
    IReadOnlyList<CityRow> Cities,
    IReadOnlyList<ProviderRow> Providers,
    IReadOnlyList<LanguagePairRow> LanguagePairs);

/// <summary>
/// Everything the admin dashboard's overview page charts, in one round trip.
///
/// User metrics exclude the "Bantera AI" system account. Registration counts only cover
/// accounts that still exist, because deleted accounts are removed entirely.
/// </summary>
public class AdminAnalyticsService(AppDbContext db)
{
    private static readonly int[] AllowedRanges = [7, 30, 90, 365];

    public static int NormalizeRange(int days)
        => AllowedRanges.Contains(days) ? days : 30;

    public async Task<AdminAnalytics> GetAsync(int requestedDays, CancellationToken ct = default)
    {
        var days = NormalizeRange(requestedDays);
        var bucket = days <= 90 ? McpBuckets.Day : McpBuckets.Week;

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var fromDate = today.AddDays(-(days - 1));
        var fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtc = today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var previousFromUtc = fromUtc.AddDays(-days);

        var users = db.Users.Where(u => u.DeletedAt == null && u.Role != "system");

        var liveSince = await db.UserActivityDaily
            .Where(a => a.Source == "live")
            .MinAsync(a => (DateOnly?)a.Date, ct);

        var kpis = await BuildKpisAsync(users, today, fromUtc, toUtc, previousFromUtc, ct);

        return new AdminAnalytics(
            now,
            days,
            bucket,
            liveSince,
            kpis,
            await SignupSeriesAsync(bucket, fromUtc, toUtc, ct),
            await ActiveSeriesAsync(fromDate, today, liveSince, ct),
            await ContentSeriesAsync(bucket, fromUtc, toUtc, ct),
            await LanguagesAsync(users, u => u.NativeLanguage, ct),
            await LanguagesAsync(users, u => u.LearningLanguage, ct),
            await CountriesAsync(ct),
            await UsersWithoutLocationAsync(users, ct),
            await CitiesAsync(ct),
            await ProvidersAsync(ct),
            await LanguagePairsAsync(users, ct));
    }

    // ── KPIs ──────────────────────────────────────────────────────────────────

    private async Task<AnalyticsKpis> BuildKpisAsync(
        IQueryable<Database.Entities.User> users,
        DateOnly today,
        DateTime fromUtc,
        DateTime toUtc,
        DateTime previousFromUtc,
        CancellationToken ct)
    {
        var total = await users.CountAsync(ct);
        var newUsers = await users.CountAsync(u => u.CreatedAt >= fromUtc && u.CreatedAt < toUtc, ct);
        var previous = await users.CountAsync(u => u.CreatedAt >= previousFromUtc && u.CreatedAt < fromUtc, ct);

        var content = await db.UserVideos
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), Ai = g.Count(v => v.IsAiGenerated) })
            .FirstOrDefaultAsync(ct);

        var jobs = await db.UserAudioJobs
            .Where(j => j.CreatedAt >= fromUtc && j.CreatedAt < toUtc)
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), Done = g.Count(j => j.Status == "done") })
            .FirstOrDefaultAsync(ct);

        var withPush = await db.UserPushTokens.Select(t => t.UserId).Distinct().CountAsync(ct);

        var totalContent = content?.Total ?? 0;
        var aiAudio = content?.Ai ?? 0;
        var jobTotal = jobs?.Total ?? 0;

        return new AnalyticsKpis(
            total,
            newUsers,
            previous,
            await DistinctActiveAsync(today, 1, ct),
            await DistinctActiveAsync(today, 7, ct),
            await DistinctActiveAsync(today, 30, ct),
            totalContent,
            totalContent - aiAudio,
            aiAudio,
            jobTotal,
            jobTotal == 0 ? 0 : Math.Round((jobs?.Done ?? 0) * 100.0 / jobTotal, 1),
            withPush);
    }

    private async Task<long> DistinctActiveAsync(DateOnly today, int windowDays, CancellationToken ct)
        => await db.UserActivityDaily
            .Where(a => a.Date > today.AddDays(-windowDays) && a.Date <= today)
            .Select(a => a.UserId)
            .Distinct()
            .LongCountAsync(ct);

    // ── Time series ───────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<CountPoint>> SignupSeriesAsync(
        string bucket, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var interval = McpBuckets.Interval(bucket);

        // `bucket` and `interval` come from a fixed whitelist, never from the request.
        var rows = await db.Database.SqlQueryRaw<McpSql.BucketCountRow>($$"""
            SELECT g.b AS "Bucket", COALESCE(c.n, 0) AS "Count"
            FROM generate_series(date_trunc('{{bucket}}', {0}::timestamptz AT TIME ZONE 'UTC'), {1}::timestamptz AT TIME ZONE 'UTC', interval '{{interval}}') AS g(b)
            LEFT JOIN (
              SELECT date_trunc('{{bucket}}', "CreatedAt" AT TIME ZONE 'UTC') AS b, COUNT(*) AS n
              FROM users
              WHERE "DeletedAt" IS NULL AND "Role" <> 'system'
                AND "CreatedAt" >= {0} AND "CreatedAt" < {2}
              GROUP BY 1
            ) c ON c.b = g.b
            ORDER BY g.b
            """, fromUtc, toUtc.AddSeconds(-1), toUtc).ToListAsync(ct);

        return [.. rows.Select(r => new CountPoint(DateOnly.FromDateTime(r.Bucket), r.Count))];
    }

    private async Task<IReadOnlyList<ActivePoint>> ActiveSeriesAsync(
        DateOnly from, DateOnly to, DateOnly? liveSince, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<McpSql.ActiveRow>("""
            SELECT d::date AS "Date",
              (SELECT COUNT(DISTINCT a."UserId") FROM user_activity_daily a
                WHERE a."Date" = d::date) AS "Dau",
              (SELECT COUNT(DISTINCT a."UserId") FROM user_activity_daily a
                WHERE a."Date" > d::date - 7 AND a."Date" <= d::date) AS "Wau",
              (SELECT COUNT(DISTINCT a."UserId") FROM user_activity_daily a
                WHERE a."Date" > d::date - 30 AND a."Date" <= d::date) AS "Mau"
            FROM generate_series({0}::date, {1}::date, interval '1 day') AS d
            ORDER BY 1
            """, from, to).ToListAsync(ct);

        return [.. rows.Select(r => new ActivePoint(
            r.Date, r.Dau, r.Wau, r.Mau,
            Approximate: liveSince is null || r.Date < liveSince))];
    }

    private async Task<IReadOnlyList<ContentPoint>> ContentSeriesAsync(
        string bucket, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var interval = McpBuckets.Interval(bucket);

        var rows = await db.Database.SqlQueryRaw<McpSql.BucketPairRow>($$"""
            SELECT g.b AS "Bucket",
                   COALESCE(c.uploads, 0) AS "CountA",
                   COALESCE(c.ai, 0) AS "CountB"
            FROM generate_series(date_trunc('{{bucket}}', {0}::timestamptz AT TIME ZONE 'UTC'), {1}::timestamptz AT TIME ZONE 'UTC', interval '{{interval}}') AS g(b)
            LEFT JOIN (
              SELECT date_trunc('{{bucket}}', "CreatedAt" AT TIME ZONE 'UTC') AS b,
                     COUNT(*) FILTER (WHERE NOT "IsAiGenerated") AS uploads,
                     COUNT(*) FILTER (WHERE "IsAiGenerated") AS ai
              FROM user_videos
              WHERE "CreatedAt" >= {0} AND "CreatedAt" < {2}
              GROUP BY 1
            ) c ON c.b = g.b
            ORDER BY g.b
            """, fromUtc, toUtc.AddSeconds(-1), toUtc).ToListAsync(ct);

        return [.. rows.Select(r => new ContentPoint(DateOnly.FromDateTime(r.Bucket), r.CountA, r.CountB))];
    }

    // ── Audience ──────────────────────────────────────────────────────────────

    private static async Task<LanguageBreakdown> LanguagesAsync(
        IQueryable<Database.Entities.User> users,
        System.Linq.Expressions.Expression<Func<Database.Entities.User, string?>> column,
        CancellationToken ct)
    {
        var raw = await users
            .GroupBy(column)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var rows = raw.Select(r => (r.Code, r.Count)).ToList();

        var byAccent = McpLanguageGrouping.Group(rows, byFamily: false);
        var combined = McpLanguageGrouping.Group(rows, byFamily: true);
        var unset = combined.FirstOrDefault(g => g.Key == McpLanguageGrouping.UnsetKey)?.Users ?? 0;

        // Percentages over users who set the language, so "Not set" does not dilute them.
        var setTotal = rows.Sum(r => r.Count) - unset;

        return new LanguageBreakdown(
            Rebase(byAccent, setTotal),
            Rebase(combined, setTotal),
            unset);
    }

    private static IReadOnlyList<LanguageGroup> Rebase(IReadOnlyList<LanguageGroup> groups, int total)
        => [.. groups
            .Where(g => g.Key != McpLanguageGrouping.UnsetKey)
            .Select(g => g with { Pct = total == 0 ? 0 : Math.Round(g.Users * 100.0 / total, 1) })];

    /// <summary>Where users are, by last known location. Shared with the MCP location tool.</summary>
    public async Task<(IReadOnlyList<CountryRow> Countries, IReadOnlyList<CityRow> Cities, long WithoutLocation)> GetLocationsAsync(CancellationToken ct = default)
    {
        var users = db.Users.Where(u => u.DeletedAt == null && u.Role != "system");
        return (await CountriesAsync(ct), await CitiesAsync(ct), await UsersWithoutLocationAsync(users, ct));
    }

    private sealed record CountryCountRow(string Code, long Users);
    private sealed record CityCountRow(string City, string? Region, string CountryCode, long Users);

    /// <summary>Each user counted once, at their most recent known location.</summary>
    private const string LatestLocationCte = """
        WITH latest AS (
          SELECT DISTINCT ON (a."UserId") a."UserId", a."CountryCode", a."Region", a."City"
          FROM user_activity_daily a
          JOIN users u ON u."Id" = a."UserId" AND u."DeletedAt" IS NULL AND u."Role" <> 'system'
          WHERE a."CountryCode" IS NOT NULL
          ORDER BY a."UserId", a."Date" DESC
        )
        """;

    private async Task<IReadOnlyList<CountryRow>> CountriesAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<CountryCountRow>($"""
            {LatestLocationCte}
            SELECT "CountryCode" AS "Code", COUNT(*) AS "Users"
            FROM latest GROUP BY 1 ORDER BY 2 DESC, 1
            """).ToListAsync(ct);

        return [.. rows.Select(r => new CountryRow(r.Code, LanguageFlags.FromCountryCode(r.Code) ?? LanguageFlags.Globe, r.Users))];
    }

    private async Task<long> UsersWithoutLocationAsync(IQueryable<Database.Entities.User> users, CancellationToken ct)
        => await users.LongCountAsync(
            u => !db.UserActivityDaily.Any(a => a.UserId == u.Id && a.CountryCode != null), ct);

    private async Task<IReadOnlyList<CityRow>> CitiesAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<CityCountRow>($"""
            {LatestLocationCte}
            SELECT "City" AS "City", MAX("Region") AS "Region", "CountryCode" AS "CountryCode", COUNT(*) AS "Users"
            FROM latest WHERE "City" IS NOT NULL
            GROUP BY "City", "CountryCode" ORDER BY 4 DESC, 1
            LIMIT 15
            """).ToListAsync(ct);

        return [.. rows.Select(r => new CityRow(
            r.City, r.Region, r.CountryCode,
            LanguageFlags.FromCountryCode(r.CountryCode) ?? LanguageFlags.Globe,
            r.Users))];
    }

    private async Task<IReadOnlyList<ProviderRow>> ProvidersAsync(CancellationToken ct)
    {
        var rows = await db.UserIdentities
            .Where(i => db.Users.Any(u => u.Id == i.UserId && u.DeletedAt == null && u.Role != "system"))
            .GroupBy(i => i.Provider)
            .Select(g => new { Provider = g.Key, Users = g.Select(i => i.UserId).Distinct().Count() })
            .ToListAsync(ct);

        return [.. rows.OrderByDescending(r => r.Users).Select(r => new ProviderRow(r.Provider, r.Users))];
    }

    private static async Task<IReadOnlyList<LanguagePairRow>> LanguagePairsAsync(
        IQueryable<Database.Entities.User> users, CancellationToken ct)
    {
        var raw = await users
            .Where(u => u.NativeLanguage != null && u.NativeLanguage != ""
                     && u.LearningLanguage != null && u.LearningLanguage != "")
            .GroupBy(u => new { u.NativeLanguage, u.LearningLanguage })
            .Select(g => new { g.Key.NativeLanguage, g.Key.LearningLanguage, Count = g.Count() })
            .ToListAsync(ct);

        return [.. raw
            .Select(r => new
            {
                Native = Chat.ChatLanguageResolver.Resolve(r.NativeLanguage),
                Learning = Chat.ChatLanguageResolver.Resolve(r.LearningLanguage),
                r.Count,
            })
            .Where(r => r.Native is not null && r.Learning is not null)
            .GroupBy(r => (N: r.Native!.MatchKey, L: r.Learning!.MatchKey))
            .Select(g => new LanguagePairRow(
                g.Key.N, g.First().Native!.DisplayName, LanguageFlags.For(g.Key.N),
                g.Key.L, g.First().Learning!.DisplayName, LanguageFlags.For(g.Key.L),
                g.Sum(x => x.Count)))
            .OrderByDescending(p => p.Users)
            .Take(10)];
    }
}

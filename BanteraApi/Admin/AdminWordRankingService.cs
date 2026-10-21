using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Admin;

public sealed class WordRankingRow
{
    public long Rank { get; set; }
    public Guid UserId { get; set; }
    public string? Name { get; set; }
    public long ListenedWords { get; set; }
    public long SpokenWords { get; set; }
}

public sealed record WordRankingReport(DateTime AsOf, string Period, string Metric,
    DateOnly? FromDate, DateOnly ThroughDate, string DateReference, int Offset, int Limit,
    bool HasMore, IReadOnlyList<WordRankingRow> Users);

public class AdminWordRankingService(AppDbContext db)
{
    public const string DateReference = "Pacific/Auckland";

    public static DateOnly ReportingDate(DateTime utcNow) => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTimeFromUtc(utcNow, TimeZoneInfo.FindSystemTimeZoneById(DateReference)));

    public static DateOnly? StartDate(string period, DateOnly today) => period switch
    {
        "today" => today,
        "week" => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
        _ => null
    };

    public async Task<WordRankingReport> GetAsync(string period, string metric, int limit = 20,
        int offset = 0, CancellationToken ct = default, DateTime? utcNow = null)
    {
        if (period is not ("today" or "week" or "total") || metric is not ("listened" or "spoken"))
            throw new ArgumentException("Invalid ranking filter.");
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Clamp(offset, 0, 1_000_000);
        var now = utcNow ?? DateTime.UtcNow;
        var today = ReportingDate(now);
        var from = StartDate(period, today);
        var lowerBound = from ?? DateOnly.MinValue;
        // Both tables contain cumulative device snapshots. UNION ALL preserves
        // distinct legacy/language buckets; uploads already deduplicate retries.
        // Rank before pagination so ties retain their rank across page boundaries.
        var rows = await db.Database.SqlQuery<WordRankingRow>($"""
            WITH activity AS (
                SELECT "UserId", "ListenedWords", "SpokenWords" FROM user_word_activity
                WHERE ({period} = 'total' OR "Date" BETWEEN {lowerBound} AND {today})
                UNION ALL
                SELECT "UserId", "ListenedWords", "SpokenWords" FROM user_language_word_activity
                WHERE ({period} = 'total' OR "Date" BETWEEN {lowerBound} AND {today})
            ), totals AS (
                SELECT u."Id" AS "UserId", u."Name",
                    SUM(a."ListenedWords")::bigint AS "ListenedWords",
                    SUM(a."SpokenWords")::bigint AS "SpokenWords"
                FROM activity a JOIN users u ON u."Id" = a."UserId"
                WHERE u."DeletedAt" IS NULL AND u."Role" <> 'system'
                GROUP BY u."Id", u."Name"
            ), scored AS (
                SELECT *, CASE WHEN {metric} = 'spoken' THEN "SpokenWords" ELSE "ListenedWords" END AS score
                FROM totals
            )
            SELECT RANK() OVER (ORDER BY score DESC) AS "Rank",
                "UserId", "Name", "ListenedWords", "SpokenWords"
            FROM scored WHERE score > 0
            ORDER BY score DESC, "UserId"
            LIMIT {limit + 1} OFFSET {offset}
            """).ToArrayAsync(ct);
        return new(now, period, metric, from, today, DateReference, offset, limit,
            rows.Length > limit, rows.Take(limit).ToArray());
    }
}

using System.Security.Cryptography;
using System.Text;
using BanteraApi.Auth;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using BanteraApi.Database;
using BanteraApi.Profile;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Leaderboards;

public sealed record SpeakingSnapshot(Guid DeviceId, LanguageWordActivityDay[] Days, DateOnly? LegacyThroughDate);
public sealed record SpeakingProfileChoice(bool ShowProfile);
public sealed record SpeakingRow(string Key, long? Rank, string? Name, string? AvatarUrl, bool IsMe,
    bool ProfileHidden, long SpokenWords, string? LearningLanguage);
public sealed record SpeakingReport(DateOnly FromDate, DateOnly ThroughDate, string TimeZone, DateTime AsOf,
    int Limit, long Participants, bool IncludesLegacyDates, bool ShowProfile, SpeakingRow[] Rows, SpeakingRow You,
    int Offset, bool HasMore);
public sealed class SpeakingSqlRow
{
    public Guid UserId { get; set; }
    public string? Name { get; set; }
    public string? AvatarObjectKey { get; set; }
    public DateTime? AvatarUpdatedAt { get; set; }
    public long SpokenWords { get; set; }
    public long Rank { get; set; }
    public long Position { get; set; }
    public long Participants { get; set; }
    public bool IncludesLegacyDates { get; set; }
    public string? LearningLanguage { get; set; }
}

public sealed class SpeakingLeaderboardService(AppDbContext db, LinkGenerator links, IOptions<JwtSettings> settings)
{
    public static (DateOnly From, DateOnly Through) Window(DateTime utcNow, int days = 7)
    {
        if (days is not (7 or 30)) throw new ArgumentOutOfRangeException(nameof(days));
        var today = DateOnly.FromDateTime(utcNow.ToUniversalTime());
        return (today.AddDays(1 - days), today);
    }

    public async Task<SpeakingReport> GetAsync(Guid owner, HttpContext context, CancellationToken ct, DateTime? at = null, int offset = 0, int days = 7)
    {
        var now = at ?? DateTime.UtcNow;
        var (from, through) = Window(now, days);
        var end = offset + 100;
        var rows = await db.Database.SqlQuery<SpeakingSqlRow>($"""
            WITH activity AS (
                SELECT s."UserId", s."Date", s."Language", s."SpokenWords",
                    (d."LegacyThroughDate" IS NOT NULL AND s."Date" <= d."LegacyThroughDate") AS legacy
                FROM speaking_days s JOIN speaking_devices d USING ("UserId", "DeviceId")
                WHERE s."Date" BETWEEN {from} AND {through}
                UNION ALL
                SELECT a."UserId", a."Date", a."Language", a."SpokenWords", true
                FROM user_language_word_activity a WHERE a."Date" BETWEEN {from} AND {through}
                AND NOT EXISTS (SELECT 1 FROM speaking_devices d WHERE d."UserId" = a."UserId" AND d."DeviceId" = a."DeviceId")
                UNION ALL
                SELECT a."UserId", a."Date", ''::text, a."SpokenWords", true
                FROM user_word_activity a WHERE a."Date" BETWEEN {from} AND {through}
                AND NOT EXISTS (SELECT 1 FROM speaking_devices d WHERE d."UserId" = a."UserId" AND d."DeviceId" = a."DeviceId")
            ), languages AS (
                SELECT "UserId", "Language", SUM("SpokenWords")::bigint words, bool_or(legacy) legacy
                FROM activity GROUP BY "UserId", "Language" HAVING SUM("SpokenWords") > 0
            ), totals AS (
                SELECT u."Id" AS "UserId", u."Name", u."AvatarObjectKey", u."AvatarUpdatedAt", u."LearningLanguage",
                    SUM(l.words)::bigint AS "SpokenWords", bool_or(l.legacy) AS "IncludesLegacyDates"
                FROM languages l JOIN users u ON u."Id" = l."UserId"
                WHERE u."DeletedAt" IS NULL AND u."Role" <> 'system' AND u."Status" = 'active'
                GROUP BY u."Id", u."Name", u."AvatarObjectKey", u."AvatarUpdatedAt", u."LearningLanguage"
            ), ranked AS (
                SELECT *, RANK() OVER (ORDER BY "SpokenWords" DESC) AS "Rank",
                    ROW_NUMBER() OVER (ORDER BY "SpokenWords" DESC, "UserId") AS "Position",
                    COUNT(*) OVER () AS "Participants" FROM totals
            )
            SELECT * FROM ranked WHERE ("Position" > {offset} AND "Position" <= {end}) OR "UserId" = {owner} ORDER BY "Position"
            """).ToArrayAsync(ct);
        SpeakingRow Map(SpeakingSqlRow row) => new(
            Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.Value.Secret),
                Encoding.UTF8.GetBytes($"speaking:{through:yyyy-MM-dd}:{row.UserId}"))).ToLowerInvariant(), row.Rank,
            row.Name,
            row.AvatarObjectKey != null
                ? links.GetUriByName(context, "GetUserAvatar", new { userId = row.UserId, v = row.AvatarUpdatedAt?.Ticks }) : null,
            row.UserId == owner, false, row.SpokenWords,
            row.LearningLanguage);
        var own = rows.FirstOrDefault(x => x.UserId == owner);
        var ownLanguage = own?.LearningLanguage ?? await db.Users.AsNoTracking()
            .Where(x => x.Id == owner).Select(x => x.LearningLanguage).FirstOrDefaultAsync(ct);
        return new(from, through, "UTC", now, 100, rows.FirstOrDefault()?.Participants ?? 0,
            rows.Any(x => x.IncludesLegacyDates), true, rows.Where(x => x.Position > offset && x.Position <= end).Select(Map).ToArray(),
            own == null ? new("you", null, null, null, true, false, 0, ownLanguage) : Map(own),
            offset, (rows.FirstOrDefault()?.Participants ?? 0) > end);
    }
}

public static class SpeakingLeaderboardEndpoints
{
    static Guid? Owner(ClaimsPrincipal user) => Guid.TryParse(user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public static bool Valid(SpeakingSnapshot? s, DateOnly today) => s is { DeviceId: var id, Days: { Length: <= 1024 } days }
        && id != Guid.Empty && (s.LegacyThroughDate == null || s.LegacyThroughDate <= today.AddDays(1))
        && days.All(d => WordActivityEndpoints.IsValid(new LanguageWordActivitySnapshot(id, [d]), today))
        && days.Select(d => (d.Date, WordActivityEndpoints.NormalizeLanguage(d.Language))).Distinct().Count() == days.Length;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/speaking-leaderboard", async (ClaimsPrincipal user, SpeakingLeaderboardService service, HttpContext context, int? offset, int? days, CancellationToken ct) => {
            if (Owner(user) is not { } owner) return Results.Unauthorized();
            if (offset is < 0 or > 10000000) return Results.BadRequest(new { message = "Invalid ranking page." });
            if (days is not (null or 7 or 30)) return Results.BadRequest(new { message = "Invalid ranking period." });
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.GetAsync(owner, context, ct, offset: offset ?? 0, days: days ?? 7));
        }).RequireAuthorization();
        // Retain the previous development client's route without accepting a hide
        // request that this release no longer supports. Existing preferences stay stored.
        app.MapPut("/api/me/speaking-leaderboard", (ClaimsPrincipal user, SpeakingProfileChoice choice, HttpResponse response) => {
            if (Owner(user) is null) return Results.Unauthorized();
            response.Headers.CacheControl = "no-store";
            return choice.ShowProfile
                ? Results.Ok(new { showProfile = true })
                : Results.Conflict(new { message = "Leaderboard profiles are visible in this version." });
        }).RequireAuthorization();
        app.MapPost("/api/me/speaking-activity", async (ClaimsPrincipal user, SpeakingSnapshot snapshot, AppDbContext db, CancellationToken ct) => {
            if (Owner(user) is not { } owner) return Results.Unauthorized();
            if (!Valid(snapshot, DateOnly.FromDateTime(DateTime.UtcNow))) return Results.BadRequest(new { message = "Invalid speaking activity." });
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO speaking_devices ("UserId", "DeviceId", "LegacyThroughDate")
                VALUES ({owner}, {snapshot.DeviceId}, {snapshot.LegacyThroughDate}) ON CONFLICT DO NOTHING
                """, ct);
            foreach (var day in snapshot.Days) {
                var language = WordActivityEndpoints.NormalizeLanguage(day.Language);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO speaking_days ("UserId", "DeviceId", "Date", "Language", "SpokenWords")
                    VALUES ({owner}, {snapshot.DeviceId}, {day.Date}, {language}, {day.SpokenWords})
                    ON CONFLICT ("UserId", "DeviceId", "Date", "Language") DO UPDATE SET
                    "SpokenWords" = GREATEST(speaking_days."SpokenWords", EXCLUDED."SpokenWords")
                    """, ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { saved = true });
        }).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(262144));
    }
}

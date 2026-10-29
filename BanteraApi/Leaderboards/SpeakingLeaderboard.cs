using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using BanteraApi.Auth;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using BanteraApi.Database;
using BanteraApi.Profile;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Leaderboards;

public sealed record SpeakingSnapshot(Guid DeviceId, LanguageWordActivityDay[] Days, DateOnly? LegacyThroughDate, SpeakingLocalDay? LocalDay = null);
public sealed record SpeakingProfileChoice(bool ShowProfile);
public sealed record SpeakingRow(string Key, long? Rank, string? Name, string? AvatarUrl, bool IsMe,
    bool ProfileHidden, long SpokenWords, string? LearningLanguage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? AdminUserId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NativeLanguage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NativeLanguageFlag = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LearningLanguageFlag = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IpCountryCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IpCountryFlag = null);
public sealed record SpeakingReport(DateOnly FromDate, DateOnly ThroughDate, string TimeZone, DateTime AsOf,
    int Limit, long Participants, bool IncludesLegacyDates, bool ShowProfile, SpeakingRow[] Rows, SpeakingRow You,
    int Offset, bool HasMore, bool CanChoosePeriod = false, DateTime? FromUtc = null, DateTime? ThroughUtc = null, int PeriodDays = 7);
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
        if (days is not (1 or 7 or 30)) throw new ArgumentOutOfRangeException(nameof(days));
        var today = DateOnly.FromDateTime(utcNow.ToUniversalTime());
        return (today.AddDays(1 - days), today);
    }

    public async Task<SpeakingReport> GetAsync(Guid owner, HttpContext context, CancellationToken ct, DateTime? at = null, int offset = 0, int days = 7, int? rollingHours = null)
    {
        var now = at ?? DateTime.UtcNow;
        var (from, through) = Window(now, days);
        var since = now.AddHours(-(rollingHours ?? 24));
        if (rollingHours != null) from = DateOnly.FromDateTime(since);
        var end = offset + 100;
        var rows = await db.Database.SqlQuery<SpeakingSqlRow>($"""
            WITH daily AS (
                SELECT s."UserId", s."DeviceId", s."Date", s."Language", s."SpokenWords",
                    (d."LegacyThroughDate" IS NOT NULL AND s."Date" <= d."LegacyThroughDate") AS legacy
                FROM speaking_days s JOIN speaking_devices d USING ("UserId", "DeviceId")
                WHERE s."Date" BETWEEN {from} AND {through}
                UNION ALL
                SELECT a."UserId", a."DeviceId", a."Date", a."Language", a."SpokenWords", true
                FROM user_language_word_activity a WHERE a."Date" BETWEEN {from} AND {through}
                AND NOT EXISTS (SELECT 1 FROM speaking_devices d WHERE d."UserId" = a."UserId" AND d."DeviceId" = a."DeviceId")
                UNION ALL
                SELECT a."UserId", a."DeviceId", a."Date", ''::text, a."SpokenWords", true
                FROM user_word_activity a WHERE a."Date" BETWEEN {from} AND {through}
                AND NOT EXISTS (SELECT 1 FROM speaking_devices d WHERE d."UserId" = a."UserId" AND d."DeviceId" = a."DeviceId")
            ), activity AS (
                SELECT d."UserId", d."DeviceId", d."Language",
                    CASE WHEN {rollingHours != null} THEN GREATEST(d."SpokenWords" - COALESCE((
                        SELECT SUM(e."SpokenWords") FROM speaking_events e
                        WHERE e."UserId" = d."UserId" AND e."DeviceId" = d."DeviceId" AND e."Language" = d."Language"
                          AND e."OccurredAt" >= (d."Date"::timestamp AT TIME ZONE 'UTC')
                          AND e."OccurredAt" < ((d."Date" + 1)::timestamp AT TIME ZONE 'UTC')
                    ), 0), 0)::bigint ELSE d."SpokenWords" END AS "SpokenWords", d.legacy
                FROM daily d
                -- The current UTC day's existing counts are wholly within a rolling
                -- whole-day window. They must not disappear just because older
                -- clients recorded daily totals instead of timestamped events.
                WHERE {rollingHours == null} OR (d."Date" > {from} AND
                    (d."Date" < {through} OR (d."Date" = {through} AND NOT d.legacy)))
                UNION ALL
                SELECT e."UserId", e."DeviceId", e."Language", e."SpokenWords", false
                FROM speaking_events e
                WHERE {rollingHours != null} AND e."OccurredAt" > {since} AND e."OccurredAt" <= {now}
            ), local_day_bounds AS (
                SELECT "UserId", "DeviceId", "Language", "FromUtc", "ThroughUtc", "SpokenWords", false legacy
                FROM speaking_day_windows
                UNION ALL
                -- Old clients already upload local calendar totals. Without a
                -- timezone, UTC +/-14 hours gives conservative possible bounds.
                -- Counts already saved have happened by this refresh, so clamp
                -- the end to now. Never assign individual recording timestamps.
                SELECT a."UserId", a."DeviceId", a."Language",
                    (a."Date"::timestamp AT TIME ZONE 'UTC') - interval '14 hours',
                    LEAST(((a."Date" + 1)::timestamp AT TIME ZONE 'UTC') + interval '14 hours', {now}),
                    a."SpokenWords", true
                FROM user_language_word_activity a
                WHERE {rollingHours != null} AND a."Date" BETWEEN {from} AND {through.AddDays(1)}
                UNION ALL
                SELECT a."UserId", a."DeviceId", ''::text,
                    (a."Date"::timestamp AT TIME ZONE 'UTC') - interval '14 hours',
                    LEAST(((a."Date" + 1)::timestamp AT TIME ZONE 'UTC') + interval '14 hours', {now}),
                    a."SpokenWords", true
                FROM user_word_activity a
                WHERE {rollingHours != null} AND a."Date" BETWEEN {from} AND {through.AddDays(1)}
            ), legacy_local_totals AS (
                SELECT "UserId", "DeviceId", "Language", SUM("SpokenWords")::bigint words
                FROM local_day_bounds
                WHERE {rollingHours != null} AND legacy AND "FromUtc">={since}
                  AND "FromUtc"<="ThroughUtc" AND "ThroughUtc"<={now}
                GROUP BY "UserId", "DeviceId", "Language"
            ), device_candidates AS (
                SELECT "UserId", "DeviceId", "Language", SUM("SpokenWords")::bigint words, bool_or(legacy) legacy
                FROM activity GROUP BY "UserId", "DeviceId", "Language"
                UNION ALL
                -- A whole local-day interval inside the window is another proven
                -- lower bound. Add only events outside it. MAX, not SUM, merges
                -- overlapping UTC/local summaries without duplicating any words.
                SELECT w."UserId", w."DeviceId", w."Language", w."SpokenWords" + COALESCE((
                    SELECT SUM(e."SpokenWords") FROM speaking_events e
                    WHERE e."UserId"=w."UserId" AND e."DeviceId"=w."DeviceId" AND e."Language"=w."Language"
                      AND e."OccurredAt">{since} AND e."OccurredAt"<={now}
                      AND (e."OccurredAt"<w."FromUtc" OR e."OccurredAt">w."ThroughUtc")
                ),0)::bigint, w.legacy
                FROM local_day_bounds w
                WHERE {rollingHours != null} AND w."FromUtc">={since} AND w."FromUtc"<=w."ThroughUtc" AND w."ThroughUtc"<={now}
                UNION ALL
                -- Multiple distinct local dates can all be safely contained.
                -- Sum those date buckets, then count each outside event once.
                SELECT t."UserId", t."DeviceId", t."Language", t.words + COALESCE((
                    SELECT SUM(e."SpokenWords") FROM speaking_events e
                    WHERE e."UserId"=t."UserId" AND e."DeviceId"=t."DeviceId" AND e."Language"=t."Language"
                      AND e."OccurredAt">{since} AND e."OccurredAt"<={now}
                      AND NOT EXISTS (
                          SELECT 1 FROM local_day_bounds w
                          WHERE w.legacy AND w."UserId"=t."UserId" AND w."DeviceId"=t."DeviceId" AND w."Language"=t."Language"
                            AND w."FromUtc">={since} AND w."FromUtc"<=w."ThroughUtc" AND w."ThroughUtc"<={now}
                            AND e."OccurredAt">=w."FromUtc" AND e."OccurredAt"<=w."ThroughUtc"
                      )
                ),0)::bigint, true
                FROM legacy_local_totals t
            ), device_totals AS (
                SELECT "UserId", "DeviceId", "Language", MAX(words) words, bool_or(legacy) legacy
                FROM device_candidates GROUP BY "UserId", "DeviceId", "Language"
            ), languages AS (
                SELECT "UserId", "Language", SUM(words)::bigint words, bool_or(legacy) legacy
                FROM device_totals GROUP BY "UserId", "Language" HAVING SUM(words) > 0
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
        // One bounded lookup for the page and the owner's row. Location stays
        // admin-only and comes from the latest recorded country, never a language.
        var admin = context.User.IsInRole("admin");
        var metadata = new Dictionary<Guid, (string? NativeLanguage, string? CountryCode)>();
        if (admin && rows.Length > 0) {
            var ids = rows.Select(r => r.UserId).ToArray();
            var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new {
                u.Id, u.NativeLanguage,
                CountryCode = db.UserActivityDaily.Where(a => a.UserId == u.Id && a.CountryCode != null)
                    .OrderByDescending(a => a.Date).Select(a => a.CountryCode).FirstOrDefault()
            }).ToArrayAsync(ct);
            metadata = people.ToDictionary(p => p.Id, p => (
                string.IsNullOrWhiteSpace(p.NativeLanguage) ? null : p.NativeLanguage,
                Activity.CloudflareGeo.NormalizeCountry(p.CountryCode)));
        }
        static string? Flag(string? language) => LanguageFlags.For(language) is var flag && flag != LanguageFlags.Globe ? flag : null;
        SpeakingRow Map(SpeakingSqlRow row) {
            metadata.TryGetValue(row.UserId, out var person);
            return new(
            Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.Value.Secret),
                Encoding.UTF8.GetBytes($"speaking:{through:yyyy-MM-dd}:{row.UserId}"))).ToLowerInvariant(), row.Rank,
            row.Name,
            row.AvatarObjectKey != null
                ? links.GetUriByName(context, "GetUserAvatar", new { userId = row.UserId, v = row.AvatarUpdatedAt?.Ticks }) : null,
            row.UserId == owner, false, row.SpokenWords,
            row.LearningLanguage, admin ? row.UserId : null, person.NativeLanguage,
            admin ? Flag(person.NativeLanguage) : null, admin ? Flag(row.LearningLanguage) : null,
            person.CountryCode, LanguageFlags.FromCountryCode(person.CountryCode));
        }
        var own = rows.FirstOrDefault(x => x.UserId == owner);
        var ownLanguage = own?.LearningLanguage ?? await db.Users.AsNoTracking()
            .Where(x => x.Id == owner).Select(x => x.LearningLanguage).FirstOrDefaultAsync(ct);
        return new(from, through, "UTC", now, 100, rows.FirstOrDefault()?.Participants ?? 0,
            rows.Any(x => x.IncludesLegacyDates), true, rows.Where(x => x.Position > offset && x.Position <= end).Select(Map).ToArray(),
            own == null ? new("you", null, null, null, true, false, 0, ownLanguage) : Map(own),
            offset, (rows.FirstOrDefault()?.Participants ?? 0) > end, admin, rollingHours != null ? since : null, rollingHours != null ? now : null,
            rollingHours is { } hours ? hours / 24 : days);
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
        SpeakingEventEndpoints.Map(app);
        SpeakingLeaderboardSettings.Map(app);
        // New clients follow the admin-configured rolling period. The released
        // fixed 7/30-day routes below keep their original interpretation.
        app.MapGet("/api/speaking-leaderboard/configured", async (ClaimsPrincipal user, SpeakingLeaderboardService service,
            AppDbContext db, HttpContext context, int? offset, DateTimeOffset? until, CancellationToken ct) => {
            if (Owner(user) is not { } owner) return Results.Unauthorized();
            if (offset is < 0 or > 10000000) return Results.BadRequest(new { message = "Invalid ranking page." });
            var now = DateTime.UtcNow;
            if (context.Request.Query.Keys.Any(key => !key.Equals("offset", StringComparison.OrdinalIgnoreCase) && !key.Equals("until", StringComparison.OrdinalIgnoreCase)) ||
                until > now || until < now.AddMinutes(-15))
                return Results.BadRequest(new { message = "Invalid ranking period." });
            context.Response.Headers.CacheControl = "no-store";
            var days = await SpeakingLeaderboardSettings.GetAsync(db, ct);
            return Results.Ok(await service.GetAsync(owner, context, ct, at: until?.UtcDateTime ?? now,
                offset: offset ?? 0, days: 30, rollingHours: days * 24));
        }).RequireAuthorization();
        app.MapGet("/api/speaking-leaderboard", async (ClaimsPrincipal user, SpeakingLeaderboardService service, HttpContext context, int? offset, int? days, int? hours, DateTimeOffset? until, CancellationToken ct) => {
            if (Owner(user) is not { } owner) return Results.Unauthorized();
            if (offset is < 0 or > 10000000) return Results.BadRequest(new { message = "Invalid ranking page." });
            if (days is not (null or 7 or 30)) return Results.BadRequest(new { message = "Invalid ranking period." });
            context.Response.Headers.CacheControl = "no-store";
            var now = DateTime.UtcNow;
            if (hours is not (null or 168 or 720) || (hours != null && days != null) ||
                (until != null && (hours == null || until > now || until < now.AddMinutes(-15))))
                return Results.BadRequest(new { message = "Invalid ranking period." });
            return Results.Ok(await service.GetAsync(owner, context, ct, at: until?.UtcDateTime ?? now,
                offset: offset ?? 0, days: days ?? 7, rollingHours: hours));
        }).RequireAuthorization();
        // Separate protected route preserves the released 7/30-day contract.
        app.MapGet("/api/admin/speaking-leaderboard", async (ClaimsPrincipal user, SpeakingLeaderboardService service, HttpContext context, int? offset, int? days, int? hours, DateTimeOffset? until, CancellationToken ct) => {
            if (Owner(user) is not { } owner) return Results.Unauthorized();
            if (offset is < 0 or > 10000000) return Results.BadRequest(new { message = "Invalid ranking page." });
            if (days is not (null or 1 or 7 or 30)) return Results.BadRequest(new { message = "Invalid ranking period." });
            context.Response.Headers.CacheControl = "no-store";
            var now = DateTime.UtcNow;
            if (hours is not (null or 24 or 168 or 720) || (hours != null && days != null) ||
                (until != null && (hours == null || until > now || until < now.AddMinutes(-15))))
                return Results.BadRequest(new { message = "Invalid ranking period." });
            return Results.Ok(await service.GetAsync(owner, context, ct,
                at: until?.UtcDateTime ?? now, offset: offset ?? 0, days: days ?? 30, rollingHours: hours));
        }).RequireAuthorization("Admin");
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
            if (!Valid(snapshot, DateOnly.FromDateTime(DateTime.UtcNow)) || !SpeakingLocalDayStorage.Valid(snapshot.LocalDay, DateTimeOffset.UtcNow)) return Results.BadRequest(new { message = "Invalid speaking activity." });
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
            if (snapshot.LocalDay is { } localDay)
                await SpeakingLocalDayStorage.SaveAsync(db, owner, snapshot.DeviceId, localDay, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { saved = true });
        }).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(262144));
    }
}

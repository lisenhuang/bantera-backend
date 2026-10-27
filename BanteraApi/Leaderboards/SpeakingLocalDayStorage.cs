using BanteraApi.Database;
using BanteraApi.Profile;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Leaderboards;

public sealed record SpeakingLocalDay(DateOnly Date, DateTimeOffset FromUtc,
    DateTimeOffset ThroughUtc, LanguageWordActivityDay[] Days);

public static class SpeakingLocalDayStorage
{
    public static bool Valid(SpeakingLocalDay? day, DateTimeOffset now) => day == null ||
        (day.Days is { Length: <= 128 } && day.FromUtc <= day.ThroughUtc &&
         day.FromUtc > now.AddDays(-35) && day.ThroughUtc <= now.AddMinutes(5) &&
         day.ThroughUtc - day.FromUtc <= TimeSpan.FromHours(26) &&
         Math.Abs((day.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - day.FromUtc.UtcDateTime).TotalHours) <= 14 &&
         day.Days.All(d => d != null && d.Date == day.Date && WordActivityEndpoints.IsValid(
             new LanguageWordActivitySnapshot(Guid.AllBitsSet, [d]), DateOnly.FromDateTime(now.UtcDateTime))) &&
         day.Days.Select(d => WordActivityEndpoints.NormalizeLanguage(d.Language)).Distinct().Count() == day.Days.Length);

    public static async Task SaveAsync(AppDbContext db, Guid owner, Guid device, SpeakingLocalDay day, CancellationToken ct)
    {
        foreach (var count in day.Days)
        {
            var language = WordActivityEndpoints.NormalizeLanguage(count.Language);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO speaking_day_windows ("UserId", "DeviceId", "Date", "Language", "FromUtc", "ThroughUtc", "SpokenWords")
                VALUES ({owner}, {device}, {day.Date}, {language}, {day.FromUtc.UtcDateTime}, {day.ThroughUtc.UtcDateTime}, {count.SpokenWords})
                ON CONFLICT ("UserId", "DeviceId", "Date", "Language") DO UPDATE SET
                    "SpokenWords"=GREATEST(speaking_day_windows."SpokenWords", EXCLUDED."SpokenWords"),
                    "FromUtc"=LEAST(speaking_day_windows."FromUtc", EXCLUDED."FromUtc"),
                    "ThroughUtc"=GREATEST(speaking_day_windows."ThroughUtc", EXCLUDED."ThroughUtc")
                """, ct);
        }
        var cutoff = day.ThroughUtc.UtcDateTime.AddDays(-35);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM speaking_day_windows WHERE "UserId"={owner} AND "ThroughUtc"<{cutoff}
            """, ct);
    }
}

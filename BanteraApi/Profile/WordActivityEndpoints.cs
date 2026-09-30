using System.Security.Claims;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Profile;

public sealed record WordActivityDay(DateOnly Date, long ListenedWords, long SpokenWords);
public sealed record WordActivitySnapshot(Guid DeviceId, WordActivityDay[] Days);

public sealed record LanguageWordActivityDay(DateOnly Date, string Language, long ListenedWords, long SpokenWords);
public sealed record LanguageWordActivitySnapshot(Guid DeviceId, LanguageWordActivityDay[] Days);

public static class WordActivityEndpoints
{
    public static bool IsValid(WordActivitySnapshot? snapshot, DateOnly utcToday) =>
        snapshot is { DeviceId: var device, Days: { Length: > 0 and <= 31 } days }
        && device != Guid.Empty
        && days.All(day => day is not null
            && day.Date >= new DateOnly(2000, 1, 1) && day.Date <= utcToday.AddDays(1)
            && day.ListenedWords is >= 0 and <= 10_000_000
            && day.SpokenWords is >= 0 and <= 10_000_000)
        && days.Select(day => day.Date).Distinct().Count() == days.Length;

    public static string NormalizeLanguage(string language)
    {
        var code = language.Trim().Replace('_', '-').ToLowerInvariant();
        if (code == "zh-hk" || code == "zh-hant-hk" || code == "yue" || code.StartsWith("yue-")) return "yue";
        var primary = code.Split('-')[0];
        return primary switch { "iw" => "he", "in" => "id", "ji" => "yi", _ => primary };
    }

    public static bool IsValid(LanguageWordActivitySnapshot? snapshot, DateOnly utcToday) =>
        snapshot is { DeviceId: var device, Days: { Length: > 0 and <= 31 } days }
        && device != Guid.Empty
        && days.All(day => day is not null && day.Language is not null && day.Language.Length <= 35
            && (day.Language.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(day.Language, @"^[a-zA-Z]{2,3}([_-][a-zA-Z0-9]{2,8})*$"))
            && day.Date >= new DateOnly(2000, 1, 1) && day.Date <= utcToday.AddDays(1)
            && day.ListenedWords is >= 0 and <= 10_000_000 && day.SpokenWords is >= 0 and <= 10_000_000)
        && days.Select(day => (day.Date, NormalizeLanguage(day.Language))).Distinct().Count() == days.Length;

    private static async Task<LanguageWordActivityDay[]> ReadDays(Guid userId, AppDbContext db, CancellationToken ct)
    {
        var legacy = await db.UserWordActivities.AsNoTracking().Where(x => x.UserId == userId)
            .GroupBy(x => x.Date).Select(g => new {
                Date = g.Key, ListenedWords = g.Sum(x => x.ListenedWords), SpokenWords = g.Sum(x => x.SpokenWords)
            }).ToArrayAsync(ct);
        var tagged = await db.Set<BanteraApi.Database.Entities.UserLanguageWordActivity>().AsNoTracking()
            .Where(x => x.UserId == userId).GroupBy(x => new { x.Date, x.Language }).Select(g => new {
                g.Key.Date, g.Key.Language, ListenedWords = g.Sum(x => x.ListenedWords), SpokenWords = g.Sum(x => x.SpokenWords)
            }).ToArrayAsync(ct);
        return legacy.Select(x => new LanguageWordActivityDay(x.Date, "", x.ListenedWords, x.SpokenWords))
            .Concat(tagged.Select(x => new LanguageWordActivityDay(x.Date, x.Language, x.ListenedWords, x.SpokenWords)))
            .OrderBy(x => x.Date).ThenBy(x => x.Language).ToArray();
    }

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/me/word-stats", async (ClaimsPrincipal user, WordActivitySnapshot snapshot,
            AppDbContext db, CancellationToken ct) =>
        {
            if (!Guid.TryParse((user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier)), out var userId))
                return Results.Unauthorized();
            if (!IsValid(snapshot, DateOnly.FromDateTime(DateTime.UtcNow)))
                return Results.BadRequest(new { message = "Invalid word activity." });
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var day in snapshot.Days)
            {
                // Never regress a cumulative count when an older request is
                // retried after a newer one, even across simultaneous requests.
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO user_word_activity ("UserId", "DeviceId", "Date", "ListenedWords", "SpokenWords")
                    VALUES ({userId}, {snapshot.DeviceId}, {day.Date}, {day.ListenedWords}, {day.SpokenWords})
                    ON CONFLICT ("UserId", "DeviceId", "Date") DO UPDATE SET
                    "ListenedWords" = GREATEST(user_word_activity."ListenedWords", EXCLUDED."ListenedWords"),
                    "SpokenWords" = GREATEST(user_word_activity."SpokenWords", EXCLUDED."SpokenWords")
                    """, ct);
            }
            await transaction.CommitAsync(ct);
            return Results.Ok(new { saved = true });
        }).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16384));

        app.MapGet("/api/me/word-stats", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            if (!Guid.TryParse((user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier)), out var userId))
                return Results.Unauthorized();
            var activity = await ReadDays(userId, db, ct);
            var days = activity.GroupBy(x => x.Date).Select(g => new WordActivityDay(
                g.Key, g.Sum(x => x.ListenedWords), g.Sum(x => x.SpokenWords))).ToArray();
            return Results.Ok(new { days });
        }).RequireAuthorization();

        app.MapPost("/api/v2/me/word-stats", async (ClaimsPrincipal user, LanguageWordActivitySnapshot snapshot,
            AppDbContext db, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Results.Unauthorized();
            if (!IsValid(snapshot, DateOnly.FromDateTime(DateTime.UtcNow)))
                return Results.BadRequest(new { message = "Invalid word activity." });
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var day in snapshot.Days)
            {
                var language = NormalizeLanguage(day.Language);
                if (language.Length == 0)
                {
                    // Legacy local data retains its original identity, even if an old
                    // app already uploaded it. Do not relabel it as today's language.
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO user_word_activity ("UserId", "DeviceId", "Date", "ListenedWords", "SpokenWords")
                        VALUES ({userId}, {snapshot.DeviceId}, {day.Date}, {day.ListenedWords}, {day.SpokenWords})
                        ON CONFLICT ("UserId", "DeviceId", "Date") DO UPDATE SET
                        "ListenedWords" = GREATEST(user_word_activity."ListenedWords", EXCLUDED."ListenedWords"),
                        "SpokenWords" = GREATEST(user_word_activity."SpokenWords", EXCLUDED."SpokenWords")
                        """, ct);
                }
                else
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO user_language_word_activity ("UserId", "DeviceId", "Date", "Language", "ListenedWords", "SpokenWords")
                        VALUES ({userId}, {snapshot.DeviceId}, {day.Date}, {language}, {day.ListenedWords}, {day.SpokenWords})
                        ON CONFLICT ("UserId", "DeviceId", "Date", "Language") DO UPDATE SET
                        "ListenedWords" = GREATEST(user_language_word_activity."ListenedWords", EXCLUDED."ListenedWords"),
                        "SpokenWords" = GREATEST(user_language_word_activity."SpokenWords", EXCLUDED."SpokenWords")
                        """, ct);
                }
            }
            await transaction.CommitAsync(ct);
            return Results.Ok(new { saved = true });
        }).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16384));

        app.MapGet("/api/v2/me/word-stats", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Results.Unauthorized();
            return Results.Ok(new { days = await ReadDays(userId, db, ct) });
        }).RequireAuthorization();
    }
}

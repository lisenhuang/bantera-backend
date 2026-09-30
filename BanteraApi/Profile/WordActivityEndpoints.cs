using System.Security.Claims;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Profile;

public sealed record WordActivityDay(DateOnly Date, long ListenedWords, long SpokenWords);
public sealed record WordActivitySnapshot(Guid DeviceId, WordActivityDay[] Days);

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
            var days = await db.UserWordActivities.AsNoTracking().Where(x => x.UserId == userId)
                .GroupBy(x => x.Date).Select(g => new {
                    Date = g.Key,
                    ListenedWords = g.Sum(x => x.ListenedWords),
                    SpokenWords = g.Sum(x => x.SpokenWords)
                }).OrderBy(x => x.Date).ToArrayAsync(ct);
            return Results.Ok(new { days });
        }).RequireAuthorization();
    }
}

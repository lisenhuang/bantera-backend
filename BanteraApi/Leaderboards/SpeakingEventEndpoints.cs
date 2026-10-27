using System.Security.Claims;
using BanteraApi.Database;
using BanteraApi.Profile;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Leaderboards;

public sealed record SpeakingEventInput(Guid EventId, DateTimeOffset OccurredAt, string Language, long SpokenWords);
public sealed record SpeakingEventBatch(Guid DeviceId, SpeakingEventInput[] Events);

public static class SpeakingEventEndpoints
{
    public static bool Valid(SpeakingEventBatch? batch, DateTimeOffset now) =>
        batch is { DeviceId: var device, Events: { Length: > 0 and <= 256 } events }
        && device != Guid.Empty
        && events.All(e => e != null && e.EventId != Guid.Empty && e.SpokenWords is > 0 and <= 100000
            && e.OccurredAt > now.AddDays(-35) && e.OccurredAt <= now.AddMinutes(5)
            && WordActivityEndpoints.IsValid(new LanguageWordActivitySnapshot(device,
                [new(DateOnly.FromDateTime(now.UtcDateTime), e.Language, 0, e.SpokenWords)]), DateOnly.FromDateTime(now.UtcDateTime)))
        && events.Select(e => e.EventId).Distinct().Count() == events.Length;

    public static async Task SaveAsync(AppDbContext db, Guid owner, SpeakingEventBatch batch, DateTime now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var e in batch.Events)
        {
            var at = e.OccurredAt.UtcDateTime;
            var language = WordActivityEndpoints.NormalizeLanguage(e.Language);
            // Retries, repeated full-window syncs and out-of-order uploads must
            // never add the same recording twice or change its original time.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO speaking_events ("UserId", "DeviceId", "EventId", "OccurredAt", "Language", "SpokenWords")
                VALUES ({owner}, {batch.DeviceId}, {e.EventId}, {at}, {language}, {e.SpokenWords})
                ON CONFLICT DO NOTHING
                """, ct);
        }
        var cutoff = now.AddDays(-35);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM speaking_events WHERE "UserId" = {owner} AND "OccurredAt" < {cutoff}
            """, ct);
        await tx.CommitAsync(ct);
    }

    public static void Map(WebApplication app) =>
        app.MapPost("/api/me/speaking-events", async (ClaimsPrincipal user, SpeakingEventBatch batch, AppDbContext db, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier), out var owner))
                return Results.Unauthorized();
            var now = DateTime.UtcNow;
            if (!Valid(batch, now)) return Results.BadRequest(new { message = "Invalid speaking activity." });
            await SaveAsync(db, owner, batch, now, ct);
            return Results.Ok(new { saved = true });
        }).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(65536));
}

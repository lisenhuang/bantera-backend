using System.Security.Cryptography;
using System.Text;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.WebsiteAnalytics;

public static class WebsiteAnalyticsEndpoints
{
    public static void Map(WebApplication app)
    {
        // Only the website server can ingest. The key never appears in browser code.
        app.MapPost("/api/website-analytics/events", async (HttpContext ctx, IConfiguration config, AppDbContext db, CancellationToken ct) =>
        {
            var key = config["WebsiteAnalytics:IngestKey"];
            if (string.IsNullOrWhiteSpace(key) || key.Length < 32) return Results.StatusCode(503);
            var supplied = ctx.Request.Headers["X-Website-Analytics-Key"].ToString();
            if (supplied.Length > 256 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
                return Results.Unauthorized();
            if (ctx.Request.ContentLength > 16384) return Results.StatusCode(413);
            // Bound actual bytes too, since valid clients can send chunked bodies.
            var body = new byte[16385];
            var length = 0;
            while (length < body.Length)
            {
                var read = await ctx.Request.Body.ReadAsync(body.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }
            if (length > 16384) return Results.StatusCode(413);
            WebsiteEventInput[]? inputs;
            try { inputs = System.Text.Json.JsonSerializer.Deserialize<WebsiteEventInput[]>(body.AsSpan(0, length), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
            if (inputs is null || inputs.Length is < 1 or > 10) return Results.BadRequest();
            var rows = inputs.Select(x => x is null ? null : WebsiteEventPolicy.Normalize(x, DateTime.UtcNow)).ToArray();
            if (rows.Any(x => x is null)) return Results.BadRequest();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var e in rows.OfType<WebsiteEvent>())
            {
                // Idempotent across retries, including simultaneous requests.
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO website_events ("Id", "SessionId", "ReceivedAt", "Name", "Path", "LandingPath", "Source", "Evidence", "ReferrerHost", "Campaign", "Medium", "Language", "Device")
                    VALUES ({e.Id}, {e.SessionId}, {e.ReceivedAt}, {e.Name}, {e.Path}, {e.LandingPath}, {e.Source}, {e.Evidence}, {e.ReferrerHost}, {e.Campaign}, {e.Medium}, {e.Language}, {e.Device})
                    ON CONFLICT ("Id") DO NOTHING
                    """, ct);
            }
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }).RequireRateLimiting("website-analytics").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16384));

        app.MapGet("/api/admin/website-analytics", async (int? days, string? source, string? language,
            AppDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var range = days is 7 or 30 or 90 ? days.Value : 30;
            var from = DateTime.UtcNow.Date.AddDays(1 - range);
            var q = db.Set<WebsiteEvent>().AsNoTracking().Where(e => e.ReceivedAt >= from);
            if (!string.IsNullOrWhiteSpace(source)) q = q.Where(e => e.Source == source);
            if (!string.IsNullOrWhiteSpace(language)) q = q.Where(e => e.Language == language);
            var sessions = await q.Select(e => e.SessionId).Distinct().CountAsync(ct);
            var events = await q.GroupBy(e => e.Name).Select(g => new { Label = g.Key, Events = g.Count(), Sessions = g.Select(e => e.SessionId).Distinct().Count() }).ToListAsync(ct);
            var daily = await q.GroupBy(e => e.ReceivedAt.Date).Select(g => new { Date = g.Key, Events = g.Count(), Sessions = g.Select(e => e.SessionId).Distinct().Count() }).OrderBy(x => x.Date).ToListAsync(ct);
            var dailySeries = Enumerable.Range(0, range).Select(i =>
            {
                var date = from.AddDays(i);
                var point = daily.FirstOrDefault(p => p.Date == date);
                return new { Date = date, Events = point?.Events ?? 0, Sessions = point?.Sessions ?? 0 };
            }).ToArray();
            var sources = await q.GroupBy(e => new { e.Source, e.Evidence }).Select(g => new { Label = g.Key.Source, g.Key.Evidence, Events = g.Count(), Sessions = g.Select(e => e.SessionId).Distinct().Count(), Plays = g.Where(e => e.Name == "lesson_play").Select(e => e.SessionId).Distinct().Count(), Downloads = g.Where(e => e.Name == "download_ios" || e.Name == "download_android").Select(e => e.SessionId).Distinct().Count() }).OrderByDescending(x => x.Sessions).Take(50).ToListAsync(ct);
            var pages = await q.Where(e => e.Name == "page_view").GroupBy(e => e.Path).Select(g => new { Label = g.Key, Events = g.Count(), Sessions = g.Select(e => e.SessionId).Distinct().Count() }).OrderByDescending(x => x.Events).Take(50).ToListAsync(ct);
            var landings = await q.GroupBy(e => e.LandingPath).Select(g => new { Label = g.Key, Events = g.Count(), Sessions = g.Select(e => e.SessionId).Distinct().Count() }).OrderByDescending(x => x.Sessions).Take(50).ToListAsync(ct);
            var languages = await q.Where(e => e.Language != "").GroupBy(e => e.Language).Select(g => new { Label = g.Key, Events = g.Count(), Sessions = g.Select(e => e.SessionId).Distinct().Count() }).OrderByDescending(x => x.Sessions).Take(80).ToListAsync(ct);
            var devices = await q.GroupBy(e => e.Device).Select(g => new { Label = g.Key, Events = g.Count(), Sessions = g.Select(e => e.SessionId).Distinct().Count() }).ToListAsync(ct);
            var campaigns = await q.Where(e => e.Campaign != "").GroupBy(e => new { e.Campaign, e.Source, e.Medium }).Select(g => new { Label = g.Key.Campaign, g.Key.Source, g.Key.Medium, Events = g.Count(), Sessions = g.Select(e => e.SessionId).Distinct().Count() }).OrderByDescending(x => x.Sessions).Take(50).ToListAsync(ct);
            var recent = await q.OrderByDescending(e => e.ReceivedAt).Take(100).Select(e => new { e.Id, e.SessionId, e.ReceivedAt, e.Name, e.Path, e.Source, e.Evidence, e.Language }).ToListAsync(ct);
            return Results.Ok(new { RangeDays = range, AsOf = DateTime.UtcNow, IngestionConfigured = config["WebsiteAnalytics:IngestKey"]?.Length >= 32, Sessions = sessions, Events = events, Daily = dailySeries, Sources = sources, Pages = pages, Landings = landings, Languages = languages, Devices = devices, Campaigns = campaigns, Recent = recent });
        }).RequireAuthorization("Admin");
    }
}

public sealed class WebsiteAnalyticsCleanup(IServiceScopeFactory scopes, ILogger<WebsiteAnalyticsCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), ct);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cutoff = DateTime.UtcNow.AddDays(-90);
                await db.Set<WebsiteEvent>().Where(e => e.ReceivedAt < cutoff).ExecuteDeleteAsync(ct);
                await Task.Delay(TimeSpan.FromHours(24), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Website analytics retention cleanup failed."); }
        }
    }
}

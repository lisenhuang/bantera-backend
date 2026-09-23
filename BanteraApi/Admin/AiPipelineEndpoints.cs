using System.Text.Json;
using BanteraApi.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Admin;

/// <summary>AI audio pipeline health for the admin dashboard: what failed, where, and how often.</summary>
public static class AiPipelineEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/ai-pipeline").RequireAuthorization("Admin");

        // GET /api/admin/ai-pipeline/summary?days=7
        group.MapGet("/summary", async (AppDbContext db, CancellationToken ct, [FromQuery] int days = 7) =>
        {
            days = Math.Clamp(days, 1, 90);
            var from = DateTime.UtcNow.Date.AddDays(-(days - 1));
            var events = db.AiPipelineEvents.AsNoTracking().Where(e => e.CreatedAt >= from);

            var jobs = await db.UserAudioJobs.AsNoTracking()
                .Where(j => j.CreatedAt >= from)
                .GroupBy(j => j.Status)
                .Select(g => new { status = g.Key, count = g.Count() })
                .ToListAsync(ct);

            var byCode = await events
                .GroupBy(e => new { e.Severity, e.Stage, e.Code })
                .Select(g => new { g.Key.Severity, g.Key.Stage, g.Key.Code, Count = g.Count(), LastAt = g.Max(e => e.CreatedAt) })
                .OrderByDescending(x => x.Count)
                .ToListAsync(ct);

            var daily = await events
                .Where(e => e.Severity != "info")
                .GroupBy(e => new { Day = e.CreatedAt.Date, e.Severity })
                .Select(g => new { g.Key.Day, g.Key.Severity, Count = g.Count() })
                .ToListAsync(ct);

            var keyFailures = await events
                .Where(e => e.Code == "key_failed" && e.KeyHint != null)
                .GroupBy(e => e.KeyHint!)
                .Select(g => new { key = g.Key, count = g.Count(), lastAt = g.Max(e => e.CreatedAt) })
                .OrderByDescending(x => x.count)
                .Take(30)
                .ToListAsync(ct);

            // Word-timing quality, from the per-generation summaries.
            var completed = await events
                .Where(e => e.Code == "timing_completed" && e.DetailJson != null)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => new { e.LanguageCode, e.DetailJson })
                .Take(5000)
                .ToListAsync(ct);
            var quality = completed
                .Select(e => (Language: e.LanguageCode ?? "unknown", Detail: ParseQuality(e.DetailJson!)))
                .Where(e => e.Detail is not null)
                .GroupBy(e => e.Language)
                .Select(g => new
                {
                    languageCode = g.Key,
                    generations = g.Count(),
                    words = g.Sum(x => x.Detail!.Total),
                    exact = g.Sum(x => x.Detail!.Exact),
                    corrected = g.Sum(x => x.Detail!.Corrected),
                    estimated = g.Sum(x => x.Detail!.Estimated),
                    retried = g.Count(x => x.Detail!.Retried),
                })
                .OrderByDescending(x => x.generations)
                .ToList();

            var dayList = Enumerable.Range(0, days).Select(i => from.AddDays(i)).ToList();
            return Results.Ok(new
            {
                days,
                from,
                generations = new
                {
                    total = jobs.Sum(j => j.count),
                    done = jobs.Where(j => j.status == "done").Sum(j => j.count),
                    failed = jobs.Where(j => j.status == "failed").Sum(j => j.count),
                    processing = jobs.Where(j => j.status == "processing").Sum(j => j.count),
                },
                byCode = byCode.Select(x => new { severity = x.Severity, stage = x.Stage, code = x.Code, count = x.Count, lastAt = x.LastAt }),
                daily = dayList.Select(d => new
                {
                    date = d.ToString("yyyy-MM-dd"),
                    errors = daily.Where(x => x.Day == d && x.Severity == "error").Sum(x => x.Count),
                    warnings = daily.Where(x => x.Day == d && x.Severity == "warning").Sum(x => x.Count),
                }),
                keyFailures,
                quality,
            });
        })
        .WithName("AdminAiPipelineSummary");

        // GET /api/admin/ai-pipeline/events?days=7&severity=&stage=&code=&language=&limit=50&offset=0
        group.MapGet("/events", async (
            AppDbContext db,
            CancellationToken ct,
            [FromQuery] int days = 7,
            [FromQuery] string? severity = null,
            [FromQuery] string? stage = null,
            [FromQuery] string? code = null,
            [FromQuery] string? language = null,
            [FromQuery] int limit = 50,
            [FromQuery] int offset = 0) =>
        {
            days = Math.Clamp(days, 1, 90);
            limit = Math.Clamp(limit, 1, 200);
            offset = Math.Max(0, offset);
            var from = DateTime.UtcNow.Date.AddDays(-(days - 1));

            var query = db.AiPipelineEvents.AsNoTracking().Where(e => e.CreatedAt >= from);
            if (!string.IsNullOrWhiteSpace(severity)) query = query.Where(e => e.Severity == severity);
            if (!string.IsNullOrWhiteSpace(stage)) query = query.Where(e => e.Stage == stage);
            if (!string.IsNullOrWhiteSpace(code)) query = query.Where(e => e.Code == code);
            if (!string.IsNullOrWhiteSpace(language)) query = query.Where(e => e.LanguageCode == language);

            var total = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(e => e.CreatedAt)
                .Skip(offset)
                .Take(limit)
                .Select(e => new
                {
                    e.Id,
                    e.CreatedAt,
                    e.Severity,
                    e.Stage,
                    e.Code,
                    e.UserId,
                    e.JobId,
                    e.Endpoint,
                    e.LanguageCode,
                    e.Model,
                    e.KeyHint,
                    e.Message,
                    e.DetailJson,
                    e.DurationMs,
                })
                .ToListAsync(ct);

            return Results.Ok(new { items, total, limit, offset });
        })
        .WithName("AdminAiPipelineEvents");
    }

    private sealed record QualityDetail(int Exact, int Corrected, int Estimated, int Total, bool Retried);

    private static QualityDetail? ParseQuality(string json)
    {
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            int Int(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
            return new QualityDetail(Int("exact"), Int("corrected"), Int("estimated"), Int("total"),
                root.TryGetProperty("retried", out var r) && r.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

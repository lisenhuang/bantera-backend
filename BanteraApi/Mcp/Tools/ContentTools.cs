using System.ComponentModel;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BanteraApi.Mcp.Tools;

/// <summary>Library and AI-generation analytics: videos, audio lessons, job outcomes.</summary>
[McpServerToolType]
public sealed class ContentTools(AppDbContext db)
{
    private static readonly Guid SystemOwnerId = Guid.Parse("816cd28a-7629-4400-948b-4e0b65bd3638");

    [McpServerTool(Name = "content_overview")]
    [Description("""
        Totals for the content library: uploaded videos versus AI-generated audio lessons,
        audio versus video media, public versus private, average duration, total storage used,
        items preserved from deleted accounts, saved items, and AI job success over 30 days.
        """)]
    public async Task<string> ContentOverviewAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var since30 = now.AddDays(-30);

        var stats = await db.UserVideos
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Ai = g.Count(v => v.IsAiGenerated),
                Uploaded = g.Count(v => !v.IsAiGenerated),
                Audio = g.Count(v => v.MediaContentType.StartsWith("audio/")),
                Public = g.Count(v => v.IsPublic),
                SystemOwned = g.Count(v => v.UserId == SystemOwnerId),
                Removed = g.Count(v => v.RemovedFromOwnerListAt != null),
                Bytes = g.Sum(v => v.FileSizeBytes),
                AvgMs = g.Average(v => (double?)v.DurationMs),
            })
            .FirstOrDefaultAsync(ct);

        var savedVideos = await db.UserSavedVideos.CountAsync(ct);
        var savedCues = await db.UserSavedCues.CountAsync(ct);

        var jobs = await db.UserAudioJobs
            .Where(j => j.CreatedAt >= since30)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Done = g.Count(j => j.Status == "done"),
                Failed = g.Count(j => j.Status == "failed"),
                Processing = g.Count(j => j.Status == "processing"),
            })
            .FirstOrDefaultAsync(ct);

        return McpJson.Serialize(new
        {
            asOf = now,
            videos = new
            {
                total = stats?.Total ?? 0,
                uploaded = stats?.Uploaded ?? 0,
                aiGenerated = stats?.Ai ?? 0,
                audio = stats?.Audio ?? 0,
                video = (stats?.Total ?? 0) - (stats?.Audio ?? 0),
                @public = stats?.Public ?? 0,
                @private = (stats?.Total ?? 0) - (stats?.Public ?? 0),
                ownedBySystemAccount = stats?.SystemOwned ?? 0,
                removedFromOwnerList = stats?.Removed ?? 0,
                avgDurationSec = stats?.AvgMs is null ? 0 : Math.Round(stats.AvgMs.Value / 1000.0, 1),
                storageBytes = stats?.Bytes ?? 0,
                storageGb = Math.Round((stats?.Bytes ?? 0) / 1024.0 / 1024 / 1024, 2),
            },
            saved = new { videos = savedVideos, cues = savedCues },
            aiJobsLast30d = new
            {
                total = jobs?.Total ?? 0,
                done = jobs?.Done ?? 0,
                failed = jobs?.Failed ?? 0,
                processing = jobs?.Processing ?? 0,
                successRatePct = SuccessRate(jobs?.Done ?? 0, jobs?.Total ?? 0),
            },
            caveats = new[] { "Items owned by the Bantera AI system account were preserved from deleted user accounts." },
        });
    }

    [McpServerTool(Name = "content_timeseries")]
    [Description("""
        Content created per bucket over time — uploads and AI audio separately — plus the
        storage added in each bucket. Gap-free: empty buckets come back as zero.
        """)]
    public async Task<string> ContentTimeseriesAsync(
        [Description("Time window, e.g. '30d', '12w'. Default '30d'.")] string? period = null,
        [Description("Bucket size: 'day', 'week', 'month' or 'auto' (default).")] string? bucket = null,
        CancellationToken ct = default)
    {
        var p = McpPeriod.Parse(period);
        var unit = McpBuckets.Validate(bucket, p);
        var interval = McpBuckets.Interval(unit);

        var rows = await db.Database.SqlQueryRaw<McpSql.ContentBucketRow>($$"""
            SELECT g.b AS "Bucket",
                   COALESCE(c.uploads, 0) AS "Uploads",
                   COALESCE(c.ai, 0) AS "AiAudio",
                   COALESCE(c.bytes, 0) AS "StorageBytes"
            FROM generate_series(date_trunc('{{unit}}', {0}::timestamptz AT TIME ZONE 'UTC'), {1}::timestamptz AT TIME ZONE 'UTC', interval '{{interval}}') AS g(b)
            LEFT JOIN (
              SELECT date_trunc('{{unit}}', "CreatedAt" AT TIME ZONE 'UTC') AS b,
                     SUM(CASE WHEN "IsAiGenerated" THEN 0 ELSE 1 END) AS uploads,
                     SUM(CASE WHEN "IsAiGenerated" THEN 1 ELSE 0 END) AS ai,
                     SUM("FileSizeBytes") AS bytes
              FROM user_videos
              WHERE "CreatedAt" >= {0} AND "CreatedAt" < {1}
              GROUP BY 1
            ) c ON c.b = g.b
            ORDER BY g.b
            """, p.FromUtc, p.ToUtc.AddSeconds(-1)).ToListAsync(ct);

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            period = new { from = p.FromUtc, to = p.ToUtc, label = p.Label },
            bucket = unit,
            totals = new
            {
                uploads = rows.Sum(r => r.Uploads),
                aiAudio = rows.Sum(r => r.AiAudio),
                storageBytes = rows.Sum(r => r.StorageBytes),
            },
            series = rows.Select(r => new
            {
                bucket = DateOnly.FromDateTime(r.Bucket),
                uploads = r.Uploads,
                aiAudio = r.AiAudio,
                storageBytes = r.StorageBytes,
            }),
        });
    }

    [McpServerTool(Name = "content_by_language")]
    [Description("""
        Content counts per transcript language, grouped into language families, with average
        duration and storage per language.
        """)]
    public async Task<string> ContentByLanguageAsync(
        [Description("Optionally restrict to items created in this window, e.g. '30d'.")] string? period = null,
        [Description("'all' (default), 'upload' for user uploads only, or 'ai' for generated audio only.")] string? kind = null,
        [Description("How many language groups to return. 1-100, default 50.")] int limit = 50,
        CancellationToken ct = default)
    {
        limit = McpPaging.Clamp(limit);
        var normalizedKind = (kind ?? "all").Trim().ToLowerInvariant();

        var query = db.UserVideos.AsQueryable();
        McpPeriod? p = null;
        if (!string.IsNullOrWhiteSpace(period))
        {
            p = McpPeriod.Parse(period);
            query = query.Where(v => v.CreatedAt >= p.FromUtc && v.CreatedAt < p.ToUtc);
        }

        query = normalizedKind switch
        {
            "upload" => query.Where(v => !v.IsAiGenerated),
            "ai" => query.Where(v => v.IsAiGenerated),
            "all" => query,
            _ => throw new McpException("kind must be 'all', 'upload' or 'ai'."),
        };

        var rows = await query
            .GroupBy(v => v.TranscriptLanguageCode)
            .Select(g => new
            {
                Code = g.Key,
                Count = g.Count(),
                AvgMs = g.Average(v => (double?)v.DurationMs),
                Bytes = g.Sum(v => v.FileSizeBytes),
            })
            .ToListAsync(ct);

        var groups = McpLanguageGrouping.Group(rows.Select(r => ((string?)r.Code, r.Count)));

        // Re-attach duration/storage per family.
        var extras = rows
            .GroupBy(r => Chat.ChatLanguageResolver.Resolve(r.Code)?.MatchKey ?? McpLanguageGrouping.UnsetKey)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    avgSec = Math.Round(g.Where(x => x.AvgMs.HasValue).Sum(x => x.AvgMs!.Value * x.Count)
                                        / Math.Max(1, g.Sum(x => x.Count)) / 1000.0, 1),
                    bytes = g.Sum(x => x.Bytes),
                },
                StringComparer.OrdinalIgnoreCase);

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            kind = normalizedKind,
            period = p is null ? null : new { from = p.FromUtc, to = p.ToUtc, label = p.Label },
            totalItems = rows.Sum(r => r.Count),
            groups = groups.Take(limit).Select(g => new
            {
                g.Key,
                g.DisplayName,
                items = g.Users,
                g.Pct,
                avgDurationSec = extras.GetValueOrDefault(g.Key)?.avgSec ?? 0,
                storageBytes = extras.GetValueOrDefault(g.Key)?.bytes ?? 0,
            }),
        });
    }

    [McpServerTool(Name = "ai_jobs_stats")]
    [Description("""
        AI audio generation outcomes: totals, success and failure rates, average completion
        time, grouped by language, scenario or day, plus the most common error messages.
        Use to answer "is AI generation failing" or "which language fails most".
        """)]
    public async Task<string> AiJobsStatsAsync(
        [Description("Time window, e.g. '30d'. Default '30d'.")] string? period = null,
        [Description("Grouping: 'language' (default), 'scenario' or 'day'.")] string? groupBy = null,
        [Description("How many groups to return. 1-100, default 50.")] int limit = 50,
        CancellationToken ct = default)
    {
        var p = McpPeriod.Parse(period);
        limit = McpPaging.Clamp(limit);
        var dimension = (groupBy ?? "language").Trim().ToLowerInvariant();

        var column = dimension switch
        {
            "language" => "\"LanguageCode\"",
            "scenario" => "\"ScenarioId\"",
            "day" => "to_char(date_trunc('day', \"CreatedAt\" AT TIME ZONE 'UTC'), 'YYYY-MM-DD')",
            _ => throw new McpException("groupBy must be 'language', 'scenario' or 'day'."),
        };

        var groups = await db.Database.SqlQueryRaw<McpSql.GroupStatRow>($$"""
            SELECT {{column}} AS "Key",
                   COUNT(*) AS "Total",
                   COUNT(*) FILTER (WHERE "Status" = 'done') AS "Done",
                   COUNT(*) FILTER (WHERE "Status" = 'failed') AS "Failed",
                   COUNT(*) FILTER (WHERE "Status" = 'processing') AS "Processing",
                   AVG(EXTRACT(EPOCH FROM ("CompletedAt" - "CreatedAt"))) AS "AvgSeconds"
            FROM user_audio_jobs
            WHERE "CreatedAt" >= {0} AND "CreatedAt" < {1}
            GROUP BY 1
            ORDER BY 2 DESC
            """, p.FromUtc, p.ToUtc).ToListAsync(ct);

        var errors = await db.Database.SqlQueryRaw<McpSql.TextCountRow>("""
            SELECT LEFT("ErrorMessage", 200) AS "Text", COUNT(*) AS "Count"
            FROM user_audio_jobs
            WHERE "Status" = 'failed' AND "ErrorMessage" IS NOT NULL
              AND "CreatedAt" >= {0} AND "CreatedAt" < {1}
            GROUP BY 1 ORDER BY 2 DESC LIMIT 10
            """, p.FromUtc, p.ToUtc).ToListAsync(ct);

        var total = groups.Sum(g => g.Total);
        var done = groups.Sum(g => g.Done);

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            period = new { from = p.FromUtc, to = p.ToUtc, label = p.Label },
            groupBy = dimension,
            totals = new
            {
                total,
                done,
                failed = groups.Sum(g => g.Failed),
                processing = groups.Sum(g => g.Processing),
                successRatePct = SuccessRate(done, total),
            },
            groups = groups.Take(limit).Select(g => new
            {
                key = g.Key ?? "(none)",
                displayName = dimension == "language" ? Chat.ChatLanguageResolver.Resolve(g.Key)?.DisplayName : null,
                total = g.Total,
                done = g.Done,
                failed = g.Failed,
                processing = g.Processing,
                successRatePct = SuccessRate(g.Done, g.Total),
                avgSeconds = g.AvgSeconds is null ? null : (double?)Math.Round(g.AvgSeconds.Value, 1),
            }),
            topErrors = errors.Select(e => new { message = e.Text, count = e.Count }),
        });
    }

    [McpServerTool(Name = "diagnostics_summary")]
    [Description("""
        AI short-cue diagnostics: how often transcript short cues were dropped, grouped by
        reason and by language. Useful for spotting transcription-quality regressions.
        """)]
    public async Task<string> DiagnosticsSummaryAsync(
        [Description("Time window, e.g. '30d'. Default '30d'.")] string? period = null,
        [Description("How many rows per grouping. 1-100, default 30.")] int limit = 30,
        CancellationToken ct = default)
    {
        var p = McpPeriod.Parse(period);
        limit = McpPaging.Clamp(limit);

        var scoped = db.AiAudioShortCueDiagnostics
            .Where(d => d.CreatedAt >= p.FromUtc && d.CreatedAt < p.ToUtc);

        var total = await scoped.CountAsync(ct);

        var byReason = await scoped
            .GroupBy(d => d.Reason)
            .Select(g => new { reason = g.Key, count = g.Count() })
            .OrderByDescending(g => g.count).Take(limit).ToListAsync(ct);

        var byLanguageReason = await scoped
            .GroupBy(d => new { d.LanguageCode, d.Reason })
            .Select(g => new { language = g.Key.LanguageCode, reason = g.Key.Reason, count = g.Count() })
            .OrderByDescending(g => g.count).Take(limit).ToListAsync(ct);

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            period = new { from = p.FromUtc, to = p.ToUtc, label = p.Label },
            total,
            byReason,
            byLanguageReason,
        });
    }

    [McpServerTool(Name = "video_lookup")]
    [Description("""
        Find videos or AI audio items by id, owner user id, or filename substring. Returns
        compact metadata only — never transcripts or storage keys. Set
        includeTranscriptPreview with a single videoId for the first 300 transcript characters.
        """)]
    public async Task<string> VideoLookupAsync(
        [Description("Exact video id.")] Guid? videoId = null,
        [Description("Return items owned by this user.")] Guid? userId = null,
        [Description("Case-insensitive substring of the original filename.")] string? search = null,
        [Description("Filter by AI-generated (true) or user-uploaded (false).")] bool? isAiGenerated = null,
        [Description("Filter by public (true) or private (false).")] bool? isPublic = null,
        [Description("How many items. 1-50, default 20.")] int limit = 20,
        [Description("Rows to skip for paging.")] int offset = 0,
        [Description("With a single videoId, include a short transcript preview.")] bool includeTranscriptPreview = false,
        CancellationToken ct = default)
    {
        limit = McpPaging.Clamp(limit, 50);
        offset = McpPaging.ClampOffset(offset);

        var query = db.UserVideos.AsQueryable();
        if (videoId is not null) query = query.Where(v => v.Id == videoId);
        if (userId is not null) query = query.Where(v => v.UserId == userId);
        if (isAiGenerated is not null) query = query.Where(v => v.IsAiGenerated == isAiGenerated);
        if (isPublic is not null) query = query.Where(v => v.IsPublic == isPublic);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(v => EF.Functions.ILike(v.OriginalFileName, $"%{search}%"));

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(v => v.CreatedAt)
            .Skip(offset).Take(limit)
            .Select(v => new
            {
                id = v.Id,
                userId = v.UserId,
                ownerName = db.Users.Where(u => u.Id == v.UserId).Select(u => u.Name).FirstOrDefault(),
                fileName = v.OriginalFileName,
                languageCode = v.TranscriptLanguageCode,
                language = v.TranscriptLanguage,
                isPublic = v.IsPublic,
                isAiGenerated = v.IsAiGenerated,
                isAudio = v.MediaContentType.StartsWith("audio/"),
                durationSec = v.DurationMs / 1000,
                sizeBytes = v.FileSizeBytes,
                savedByCount = db.UserSavedVideos.Count(s => s.VideoId == v.Id),
                createdAt = v.CreatedAt,
                removedFromOwnerListAt = v.RemovedFromOwnerListAt,
                transcriptPreview = includeTranscriptPreview && videoId != null
                    ? v.TranscriptText.Substring(0, v.TranscriptText.Length > 300 ? 300 : v.TranscriptText.Length)
                    : null,
            })
            .ToListAsync(ct);

        return McpJson.Serialize(new { asOf = DateTime.UtcNow, total, items });
    }

    private static double SuccessRate(long done, long total)
        => total == 0 ? 0 : Math.Round(done * 100.0 / total, 1);
}

using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Admin;

public sealed record AdminPipelineRunRow(
    string Kind, Guid Id, Guid? VideoId, Guid UserId, string? CreatorName,
    string Name, string? LanguageCode, string Status, bool? IsPublic,
    bool IsAiGenerated, int? DurationMs, long? FileSizeBytes, DateTime CreatedAt);

public sealed record AdminPipelineRunDetail(
    AdminPipelineRunRow Run, Guid? JobId, string? ScenarioId, DateTime? CompletedAt,
    string? ErrorMessage, IReadOnlyList<AiPipelineEvent> Events, bool EventsTruncated,
    int EventRetentionDays);

/// <summary>Admin-only history of saved videos and generations that failed before a video existed.</summary>
public static class AdminPipelineRunEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/ai-pipeline/runs").RequireAuthorization("Admin");

        group.MapGet("", async (
            AppDbContext db, CancellationToken ct,
            [FromQuery] string? languageCode = null,
            [FromQuery] bool? isPublic = null,
            [FromQuery] bool? isAiGenerated = null,
            [FromQuery] string? sort = null,
            [FromQuery] string? dir = null,
            [FromQuery] int limit = 20,
            [FromQuery] int offset = 0) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);

            var videos = db.UserVideos.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(languageCode))
                videos = videos.Where(v => v.TranscriptLanguageCode == languageCode);
            if (isPublic.HasValue)
                videos = videos.Where(v => v.IsPublic == isPublic.Value);
            if (isAiGenerated.HasValue)
                videos = videos.Where(v => v.IsAiGenerated == isAiGenerated.Value);

            var jobs = db.UserAudioJobs.AsNoTracking().Where(j => j.VideoId == null);
            if (!string.IsNullOrWhiteSpace(languageCode))
                jobs = jobs.Where(j => j.LanguageCode == languageCode);
            if (isPublic.HasValue || isAiGenerated == false)
                jobs = jobs.Where(_ => false);

            var videoRows = videos.Select(v => new
            {
                Kind = "video", v.Id, VideoId = (Guid?)v.Id, v.UserId,
                CreatorName = v.User.Name, Name = v.OriginalFileName,
                LanguageCode = (string?)v.TranscriptLanguageCode,
                Status = v.IsAiGenerated ? "done" : "uploaded",
                IsPublic = (bool?)v.IsPublic, v.IsAiGenerated,
                DurationMs = (int?)v.DurationMs, FileSizeBytes = (long?)v.FileSizeBytes,
                v.CreatedAt,
            });
            var jobRows = from j in jobs
                          join u in db.Users.AsNoTracking() on j.UserId equals u.Id
                          select new
                          {
                              Kind = "job", j.Id, j.VideoId, j.UserId,
                              CreatorName = u.Name, Name = j.ScenarioId ?? "AI generation",
                              LanguageCode = j.LanguageCode, j.Status,
                              IsPublic = (bool?)null, IsAiGenerated = true,
                              DurationMs = (int?)null, FileSizeBytes = (long?)null,
                              j.CreatedAt,
                          };
            var rows = videoRows.Concat(jobRows);
            var total = await rows.CountAsync(ct);
            rows = (sort?.ToLowerInvariant(), dir?.ToLowerInvariant() == "desc") switch
            {
                ("filename", false) => rows.OrderBy(r => r.Name),
                ("filename", true) => rows.OrderByDescending(r => r.Name),
                ("language", false) => rows.OrderBy(r => r.LanguageCode),
                ("language", true) => rows.OrderByDescending(r => r.LanguageCode),
                ("duration", false) => rows.OrderBy(r => r.DurationMs),
                ("duration", true) => rows.OrderByDescending(r => r.DurationMs),
                ("size", false) => rows.OrderBy(r => r.FileSizeBytes),
                ("size", true) => rows.OrderByDescending(r => r.FileSizeBytes),
                ("createdat", false) => rows.OrderBy(r => r.CreatedAt),
                _ => rows.OrderByDescending(r => r.CreatedAt),
            };
            var page = await rows.Skip(offset).Take(limit).ToListAsync(ct);
            return Results.Ok(new AdminPagedResult<AdminPipelineRunRow>(
                page.Select(r => new AdminPipelineRunRow(r.Kind, r.Id, r.VideoId, r.UserId,
                    r.CreatorName, r.Name, r.LanguageCode, r.Status, r.IsPublic,
                    r.IsAiGenerated, r.DurationMs, r.FileSizeBytes, r.CreatedAt)).ToArray(), total));
        }).WithName("AdminListPipelineRuns");

        group.MapGet("/{kind}/{id:guid}", async (string kind, Guid id, AppDbContext db, CancellationToken ct) =>
        {
            UserVideo? video = null;
            UserAudioJob? job = null;
            if (kind == "video")
            {
                video = await db.UserVideos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
                if (video is null) return Results.NotFound();
                job = await db.UserAudioJobs.AsNoTracking()
                    .Where(j => j.VideoId == video.Id)
                    .OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
            }
            else if (kind == "job")
            {
                job = await db.UserAudioJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
                if (job is null) return Results.NotFound();
                if (job.VideoId.HasValue)
                    video = await db.UserVideos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == job.VideoId, ct);
            }
            else return Results.NotFound();

            var userId = video?.UserId ?? job!.UserId;
            var creatorName = await db.Users.AsNoTracking().Where(u => u.Id == userId)
                .Select(u => u.Name).FirstOrDefaultAsync(ct);
            var row = video is not null
                ? new AdminPipelineRunRow("video", video.Id, video.Id, video.UserId, creatorName,
                    video.OriginalFileName, video.TranscriptLanguageCode,
                    video.IsAiGenerated ? "done" : "uploaded", video.IsPublic,
                    video.IsAiGenerated, video.DurationMs, video.FileSizeBytes, video.CreatedAt)
                : new AdminPipelineRunRow("job", job!.Id, null, job.UserId, creatorName,
                    job.ScenarioId ?? "AI generation", job.LanguageCode, job.Status,
                    null, true, null, null, job.CreatedAt);

            List<AiPipelineEvent> eventRows = job is null ? [] : await db.AiPipelineEvents.AsNoTracking()
                .Where(e => e.JobId == job.Id)
                .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
                .Take(1001).ToListAsync(ct);
            return Results.Ok(new AdminPipelineRunDetail(row, job?.Id, job?.ScenarioId,
                job?.CompletedAt, job?.ErrorMessage, eventRows.Take(1000).ToArray(),
                eventRows.Count > 1000, 90));
        }).WithName("AdminGetPipelineRun");
    }
}

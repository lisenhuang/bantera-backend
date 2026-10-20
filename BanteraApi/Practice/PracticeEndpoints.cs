using System.Security.Claims;
using BanteraApi.Database;
using BanteraApi.Gemini;
using BanteraApi.Profile;
using BanteraApi.Storage;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Practice;

public static class PracticeEndpoints
{
    public sealed record NextRequest(Guid RequestId, string Locale, Guid? PreviousId, bool DeferLevelScoring = false);
    public sealed record AnswerRequest(Guid AttemptId, string Transcript, string? Recognition);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/me/practice").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var userId)) return Results.Unauthorized();
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            if (!await db.Users.AnyAsync(x => x.Id == userId && x.DeletedAt == null && x.Status == "active", http.RequestAborted))
                return Results.Unauthorized();
            if (http.Request.Headers["X-Practice-Consent"] != "1") return Results.StatusCode(403);
            try { return await next(context); }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Practice")
                    .LogWarning("Practice request failed: {Type}", ex.GetType().Name);
                return Results.Json(new { code = "practice_unavailable", message = "Practice is unavailable right now. Please try again." }, statusCode: 503);
            }
        });
        group.MapPost("/next", async (NextRequest request, ClaimsPrincipal user, AppDbContext db, PracticeWorker worker, R2StorageService storage, CancellationToken ct) =>
        {
            if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Locale)) return Results.BadRequest();
            var locale = LearningLanguageCatalog.Items.FirstOrDefault(x => x.Identifier.Equals(request.Locale, StringComparison.OrdinalIgnoreCase))?.Identifier;
            if (locale is null) return Results.BadRequest();
            var userId = Guid.Parse(user.FindFirstValue("sub")!);
            var language = WordActivityEndpoints.NormalizeLanguage(locale);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await UserLock(db, userId, ct);
            var progress = await Progress(db, userId, language, ct);
            var exposures = db.Set<PracticeExposure>().Where(x => x.Dialogue.ContentVersion == PracticeRules.ContentVersion);
            var existing = await db.Set<PracticeExposure>().Include(x => x.Dialogue)
                .FirstOrDefaultAsync(x => x.UserId == userId && x.RequestId == request.RequestId, ct);
            if (existing is not null)
            {
                if (existing.Dialogue.ContentVersion == PracticeRules.ContentVersion)
                {
                    var existingProgress = existing.Dialogue.Language == language ? progress : await Progress(db, userId, existing.Dialogue.Language, ct);
                    worker.RequestAhead(userId, existing.Dialogue.Locale, existing.Dialogue.Language, existingProgress, existing);
                    return Results.Ok(Present(existing, storage));
                }
                // Release the unique request ID before replacing a retired exercise.
                // A client can retry the same request after a content refresh.
                db.Remove(existing);
                await db.SaveChangesAsync(ct);
            }
            if (request.PreviousId is { } previous)
            {
                var old = await exposures.FirstOrDefaultAsync(x => x.Id == previous && x.UserId == userId, ct);
                // An exercise retired during a content refresh must not trap Next.
                if (old is not null)
                {
                    await db.Entry(old).Reference(x => x.Dialogue).LoadAsync(ct);
                    PracticeRules.Finish(old, progress);
                }
            }
            else
            {
                var active = await exposures.Include(x => x.Dialogue).Where(x => x.UserId == userId && x.FinishedAt == null && x.Dialogue.Locale == locale)
                    .OrderByDescending(x => x.SeenAt).FirstOrDefaultAsync(ct);
                if (active is not null)
                {
                    // Preserve already-scored legacy results. Start a fresh exercise
                    // for upgraded clients so translation can exclude the whole result.
                    if (request.DeferLevelScoring && !active.DeferredScoring && active.FirstCorrect is not null)
                        active.FinishedAt = DateTime.UtcNow;
                    else
                    {
                        active.DeferredScoring |= request.DeferLevelScoring;
                        await db.SaveChangesAsync(ct);
                        await tx.CommitAsync(ct);
                        worker.RequestAhead(userId, locale, language, progress, active);
                        return Results.Ok(Present(active, storage));
                    }
                }
            }
            if (await exposures.CountAsync(x => x.UserId == userId && x.SeenAt >= DateTime.UtcNow.Date, ct) >= 200)
                return Results.Json(new { code = "practice_daily_limit" }, statusCode: 429);
            var available = db.Set<PracticeDialogue>().Where(d => d.Locale == locale && d.Level == progress.Level && d.Ready && d.ContentVersion == PracticeRules.ContentVersion &&
                !exposures.Any(e => e.UserId == userId && e.DialogueId == d.Id));
            var selected = await available.OrderBy(d => d.CreatedAt).FirstOrDefaultAsync(ct);
            PracticeExposure? exposure = null;
            if (selected is not null)
            {
                exposure = new() { UserId = userId, RequestId = request.RequestId, Dialogue = selected, DialogueId = selected.Id, Swap = Random.Shared.Next(2) == 1, DeferredScoring = request.DeferLevelScoring };
                db.Add(exposure);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            worker.RequestAhead(userId, locale, language, progress, exposure);
            return exposure is null ? Results.Json(new { status = "preparing", retryAfterSeconds = 4 }, statusCode: 202) : Results.Ok(Present(exposure, storage));
        });
        group.MapPost("/{id:guid}/answer", async (Guid id, AnswerRequest request, ClaimsPrincipal user, AppDbContext db, PracticeWorker worker, CancellationToken ct) =>
        {
            if (request.AttemptId == Guid.Empty || request.Transcript is null || request.Transcript.Length > 1200 || request.Recognition?.Length > 600)
                return Results.BadRequest();
            var userId = Guid.Parse(user.FindFirstValue("sub")!);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await UserLock(db, userId, ct);
            var exposure = await db.Set<PracticeExposure>().Include(x => x.Dialogue).FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
            if (exposure is null) return Results.NotFound();
            if (exposure.Dialogue.ContentVersion != PracticeRules.ContentVersion)
                return Results.Json(new { code = "practice_retired" }, statusCode: 410);
            var prior = await db.Set<PracticeAttempt>().FindAsync([request.AttemptId], ct);
            if (prior is not null) return prior.ExposureId == id ? Results.Ok(Feedback(prior, exposure)) : Results.Conflict();
            if (exposure.Attempts >= 10) return Results.Json(new { code = "practice_attempt_limit" }, statusCode: 429);
            // Correctness comes from the stored choice. No AI service is resolved or called.
            var judgement = PracticeAnswerMatcher.Judge(exposure.Dialogue, exposure.Swap, request.Transcript, request.Recognition);
            var attempt = new PracticeAttempt { Id = request.AttemptId, ExposureId = id, Outcome = judgement.Outcome, Choice = judgement.Choice };
            db.Add(attempt);
            exposure.Attempts++;
            var progress = await Progress(db, userId, exposure.Dialogue.Language, ct);
            PracticeRules.RecordAnswer(exposure, progress, judgement.Outcome);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            worker.RequestAhead(userId, exposure.Dialogue.Locale, exposure.Dialogue.Language, progress, exposure);
            return Results.Ok(Feedback(attempt, exposure));
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(8192));

        group.MapPost("/{id:guid}/translation", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue("sub")!);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await UserLock(db, userId, ct);
            var exposure = await db.Set<PracticeExposure>().FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
            if (exposure is null) return Results.NotFound();
            if (exposure.FinishedAt is not null || !exposure.DeferredScoring) return Results.Conflict();
            exposure.TranslationUsed = true;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { translationUsed = true });
        });
    }
    private static Task<int> UserLock(AppDbContext db, Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"practice-user:" + id}, 0))", ct);
    private static async Task<PracticeProgress> Progress(AppDbContext db, Guid userId, string language, CancellationToken ct)
    {
        var p = await db.Set<PracticeProgress>().FindAsync([userId, language], ct);
        if (p is not null) return p;
        p = new() { UserId = userId, Language = language }; db.Add(p); return p;
    }
    private static object Present(PracticeExposure e, R2StorageService storage)
    {
        var d = e.Dialogue;
        object Choice(string text, string key) => new { text, audioUrl = storage.GeneratePresignedUrl(key, TimeSpan.FromHours(4)) };
        var choices = new[] { Choice(d.Reply, d.ReplyAudio), Choice(d.Distractor, d.DistractorAudio) };
        if (e.Swap) Array.Reverse(choices);
        return new { status = "ready", id = e.Id, locale = d.Locale, level = PracticeRules.Levels[d.Level], cue = d.Cue,
            cueAudioUrl = storage.GeneratePresignedUrl(d.CueAudio, TimeSpan.FromHours(4)), choices,
            deferredScoring = e.DeferredScoring, translationUsed = e.TranslationUsed };
    }
    private static object Feedback(PracticeAttempt a, PracticeExposure e) => new {
        outcome = a.Outcome, choice = a.Choice,
        explanation = a.Outcome == "unclear" ? null : e.Dialogue.Explanation
    };
}

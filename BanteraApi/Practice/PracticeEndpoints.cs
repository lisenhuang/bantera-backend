using System.Security.Claims;
using BanteraApi.Database;
using BanteraApi.Gemini;
using BanteraApi.Profile;
using BanteraApi.Storage;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Practice;

public static class PracticeEndpoints
{
    public sealed record NextRequest(Guid RequestId, string Locale, Guid? PreviousId, bool DeferLevelScoring = false, bool UseSelectedLevel = false, bool SupportsA0 = false, string? ListeningLevel = null);
    public sealed record LevelRequest(string Locale, string Level, bool SupportsA0 = false);
    public sealed record AnswerRequest(Guid AttemptId, string Transcript, string? Recognition);
    public static void MapAdmin(WebApplication app)
    {
        app.MapDelete("/api/admin/practice/exposures/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, R2StorageService storage, ILogger<PracticeDialogue> logger, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var owner)) return Results.Unauthorized();
            var dialogue = await PracticeAdminDeletion.DeleteAsync(db, owner, id, ct);
            if (dialogue == null) return Results.NotFound();
            foreach (var key in new[] { dialogue.CueAudio, dialogue.ReplyAudio, dialogue.DistractorAudio }.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct())
            {
                try { await storage.DeleteObjectAsync(key, ct); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not remove audio for deleted practice {DialogueId}", dialogue.Id); }
            }
            return Results.NoContent();
        }).RequireAuthorization("Admin");
    }

    public static void Map(WebApplication app)
    {
        MapAdmin(app);
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
        group.MapPost("/{id:guid}/english-translation", async (Guid id, ClaimsPrincipal user, AppDbContext db, PracticeWorker worker, CancellationToken ct) =>
        {
            var owner = Guid.Parse(user.FindFirstValue("sub")!);
            var exposure = await db.Set<PracticeExposure>().AsNoTracking().Include(e => e.Dialogue)
                .FirstOrDefaultAsync(e => e.Id == id && e.UserId == owner, ct);
            if (exposure == null || !MaoriPracticeTranslation.Applies(exposure.Dialogue.Locale)) return Results.NotFound();
            if (exposure.Dialogue.ContentVersion != PracticeRules.ContentVersion) return Results.StatusCode(410);
            var translated = MaoriPracticeTranslation.Present(exposure);
            if (translated != null) return Results.Ok(new { status = "ready", translations = translated, translationLocale = "en-NZ" });
            var d = exposure.Dialogue;
            worker.Request(new(owner, d.Locale, d.Language, d.Level, d.Id));
            return Results.Json(new { status = "preparing" }, statusCode: 202);
        }).RequireAuthorization("Admin");
        group.MapPost("/{id:guid}/pronunciation", async (Guid id, ClaimsPrincipal user, AppDbContext db, IServiceProvider services, CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue("sub")!);
            var exposure = await db.Set<PracticeExposure>().AsNoTracking().Include(x => x.Dialogue)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
            if (exposure is null) return Results.NotFound();
            if (exposure.Dialogue.ContentVersion != PracticeRules.ContentVersion) return Results.StatusCode(410);
            if (PracticePronunciation.Read(exposure.Dialogue) == null && PracticePronunciation.Scheme(exposure.Dialogue.Locale) != null)
            {
                if (exposure.Dialogue.PronunciationAttempts >= 5 && exposure.Dialogue.PronunciationRetryAt <= DateTime.UtcNow)
                {
                    await db.Set<PracticeDialogue>().Where(d => d.Id == exposure.DialogueId && d.PronunciationJson == null && d.PronunciationRetryAt <= DateTime.UtcNow)
                        .ExecuteUpdateAsync(s => s.SetProperty(d => d.PronunciationAttempts, 0).SetProperty(d => d.PronunciationRetryAt, (DateTime?)null), ct);
                    exposure.Dialogue.PronunciationAttempts = 0;
                }
                await db.Set<PracticeDialogue>().Where(d => d.Id == exposure.DialogueId && d.PronunciationJson == null && d.PronunciationRequestedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.PronunciationRequestedAt, DateTime.UtcNow), ct);
                services.GetService<PracticePronunciationWorker>()?.Request(exposure.DialogueId);
            }
            return Results.Ok(PracticePronunciation.Present(exposure));
        });
        group.MapPost("/level", async (LevelRequest request, ClaimsPrincipal user, AppDbContext db, PracticeWorker worker, CancellationToken ct) =>
        {
            var locale = LearningLanguageCatalog.ResolvePracticeLocale(request.Locale, user.IsInRole("admin"));
            if (locale is null) return Results.BadRequest();
            var userId = Guid.Parse(user.FindFirstValue("sub")!);
            var language = WordActivityEndpoints.NormalizeLanguage(locale);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await UserLock(db, userId, ct);
            var progress = await Progress(db, userId, language, ct, request.SupportsA0);
            // A legacy device can stay at A1 while another device is learning A0.
            // Do not overwrite that device's saved beginner progress.
            var legacyBeginner = !request.SupportsA0 && progress.Level == PracticeRules.BeginnerLevel && request.Level == "A1";
            if ((!request.SupportsA0 && request.Level == "A0") || (!legacyBeginner && !PracticeRules.SelectLevel(progress, request.Level)))
                return Results.Json(new { code = "practice_level_locked", currentLevel = PracticeRules.LevelName(PracticeRules.ClientLevel(progress.Level, request.SupportsA0)) }, statusCode: 409);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            worker.Request(new(userId, locale, language, PracticeRules.ClientLevel(PracticeRules.EffectiveLevel(progress), request.SupportsA0)));
            return Results.Ok(new { levelStates = PracticeRules.LevelStates(progress), highestUnlockedLevel = PracticeRules.LevelName(PracticeRules.HighestUnlockedLevel(progress)), currentLevel = PracticeRules.LevelName(PracticeRules.ClientLevel(progress.Level, request.SupportsA0)), practiceLevel = PracticeRules.LevelName(PracticeRules.ClientLevel(PracticeRules.EffectiveLevel(progress), request.SupportsA0)) });
        });
        group.MapPost("/next", async (NextRequest request, ClaimsPrincipal user, AppDbContext db, PracticeWorker worker, R2StorageService storage, CancellationToken ct) =>
        {
            if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Locale)) return Results.BadRequest();
            var locale = LearningLanguageCatalog.ResolvePracticeLocale(request.Locale, user.IsInRole("admin"));
            if (locale is null) return Results.BadRequest();
            var listeningLevel = request.ListeningLevel == null ? (int?)null : PracticeRules.LevelIndex(request.ListeningLevel);
            if (listeningLevel is < PracticeRules.BeginnerLevel or > 5 ||
                (listeningLevel == PracticeRules.BeginnerLevel && !request.SupportsA0)) return Results.BadRequest();
            var userId = Guid.Parse(user.FindFirstValue("sub")!);
            var language = WordActivityEndpoints.NormalizeLanguage(locale);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await UserLock(db, userId, ct);
            var progress = await Progress(db, userId, language, ct, request.SupportsA0);
            var targetLevel = listeningLevel ?? PracticeRules.ClientLevel(PracticeRules.EffectiveLevel(progress, request.UseSelectedLevel), request.SupportsA0);
            var exposures = db.Set<PracticeExposure>().Where(x => x.Dialogue.ContentVersion == PracticeRules.ContentVersion);
            var existing = await db.Set<PracticeExposure>().Include(x => x.Dialogue)
                .FirstOrDefaultAsync(x => x.UserId == userId && x.RequestId == request.RequestId, ct);
            if (existing is not null)
            {
                if (existing.Dialogue.ContentVersion == PracticeRules.ContentVersion && (request.SupportsA0 || existing.Dialogue.Level >= 0))
                {
                    var existingProgress = existing.Dialogue.Language == language ? progress : await Progress(db, userId, existing.Dialogue.Language, ct);
                    if (listeningLevel == null) worker.RequestAhead(userId, existing.Dialogue.Locale, existing.Dialogue.Language, existingProgress, existing, request.UseSelectedLevel, request.SupportsA0);
                    else worker.Request(new(userId, locale, language, targetLevel));
                    return Results.Ok(Present(existing, storage, existingProgress, request.SupportsA0));
                }
                if (existing.Dialogue.ContentVersion == PracticeRules.ContentVersion) return Results.Conflict();
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
                    PracticeRules.Finish(old, progress, request.SupportsA0);
                }
            }
            else
            {
                var active = await exposures.Include(x => x.Dialogue).Where(x => x.UserId == userId && x.FinishedAt == null && x.Dialogue.Locale == locale && (request.SupportsA0 || x.Dialogue.Level >= 0) && ((!request.UseSelectedLevel && listeningLevel == null) || x.Dialogue.Level == targetLevel) && (listeningLevel == null || x.ReviewMode) && (!x.ReviewMode || request.UseSelectedLevel || listeningLevel != null))
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
                        if (listeningLevel == null) worker.RequestAhead(userId, locale, language, progress, active, request.UseSelectedLevel, request.SupportsA0);
                        else worker.Request(new(userId, locale, language, targetLevel));
                        return Results.Ok(Present(active, storage, progress, request.SupportsA0));
                    }
                }
            }
            if (await exposures.CountAsync(x => x.UserId == userId && x.SeenAt >= DateTime.UtcNow.Date, ct) >= 200)
                return Results.Json(new { code = "practice_daily_limit" }, statusCode: 429);
            var available = db.Set<PracticeDialogue>().Where(d => d.Locale == locale && d.Level == targetLevel && d.Ready && d.ContentVersion == PracticeRules.ContentVersion &&
                !exposures.Any(e => e.UserId == userId && e.DialogueId == d.Id));
            var selected = await available.OrderBy(d => d.CreatedAt).FirstOrDefaultAsync(ct);
            PracticeExposure? exposure = null;
            if (selected is not null)
            {
                exposure = new() { UserId = userId, RequestId = request.RequestId, Dialogue = selected, DialogueId = selected.Id, Swap = Random.Shared.Next(2) == 1, DeferredScoring = request.DeferLevelScoring, ReviewMode = listeningLevel != null || targetLevel < progress.Level };
                db.Add(exposure);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (listeningLevel == null) worker.RequestAhead(userId, locale, language, progress, exposure, request.UseSelectedLevel, request.SupportsA0);
            else worker.Request(new(userId, locale, language, targetLevel));
            return exposure is null ? Results.Json(new { status = "preparing", retryAfterSeconds = 4, levelStates = PracticeRules.LevelStates(progress), highestUnlockedLevel = PracticeRules.LevelName(PracticeRules.HighestUnlockedLevel(progress)), currentLevel = PracticeRules.LevelName(PracticeRules.ClientLevel(progress.Level, request.SupportsA0)), practiceLevel = PracticeRules.LevelName(targetLevel) }, statusCode: 202) : Results.Ok(Present(exposure, storage, progress, request.SupportsA0));
        });
        // Saved reviews reuse the owned content, but never mutate the original
        // exposure or progression. Presentation always refreshes signed audio URLs.
        group.MapPost("/{id:guid}/review", async (Guid id, ClaimsPrincipal user, AppDbContext db, R2StorageService storage, CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue("sub")!);
            var source = await db.Set<PracticeExposure>().AsNoTracking().Include(x => x.Dialogue)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
            if (source is null) return Results.NotFound();
            if (!source.Dialogue.Ready || source.Dialogue.ContentVersion != PracticeRules.ContentVersion)
                return Results.Json(new { code = "practice_retired" }, statusCode: 410);
            var progress = await db.Set<PracticeProgress>().AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserId == userId && x.Language == source.Dialogue.Language, ct)
                ?? new PracticeProgress { Level = source.Dialogue.Level };
            return Results.Ok(Present(source, storage, progress, supportsA0: true, savedReview: true));
        });
        group.MapPost("/{id:guid}/review/answer", async (Guid id, AnswerRequest request, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            if (request.AttemptId == Guid.Empty || request.Transcript is null || request.Transcript.Length > 1200 || request.Recognition?.Length > 600)
                return Results.BadRequest();
            var userId = Guid.Parse(user.FindFirstValue("sub")!);
            var source = await db.Set<PracticeExposure>().AsNoTracking().Include(x => x.Dialogue)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
            if (source is null) return Results.NotFound();
            if (!source.Dialogue.Ready || source.Dialogue.ContentVersion != PracticeRules.ContentVersion)
                return Results.Json(new { code = "practice_retired" }, statusCode: 410);
            var judgement = PracticeAnswerMatcher.Judge(source.Dialogue, source.Swap, request.Transcript, request.Recognition);
            return Results.Ok(Feedback(new PracticeAttempt { Outcome = judgement.Outcome, Choice = judgement.Choice }, source, request.Transcript));
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(8192));
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
            if (prior is not null) return prior.ExposureId == id ? Results.Ok(Feedback(prior, exposure, request.Transcript)) : Results.Conflict();
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
            return Results.Ok(Feedback(attempt, exposure, request.Transcript));
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
    private static async Task<PracticeProgress> Progress(AppDbContext db, Guid userId, string language, CancellationToken ct, bool supportsA0 = false)
    {
        var p = await db.Set<PracticeProgress>().FindAsync([userId, language], ct);
        if (p is not null) return p;
        p = new() { UserId = userId, Language = language, Level = supportsA0 ? PracticeRules.BeginnerLevel : 0, HighestPassedLevel = supportsA0 ? -2 : -1 }; db.Add(p); return p;
    }
    private static object Present(PracticeExposure e, R2StorageService storage, PracticeProgress progress, bool supportsA0, bool savedReview = false)
    {
        var d = e.Dialogue;
        object Choice(string text, string key) => new { text, audioUrl = storage.GeneratePresignedUrl(key, TimeSpan.FromHours(4)) };
        var choices = new[] { Choice(d.Reply, d.ReplyAudio), Choice(d.Distractor, d.DistractorAudio) };
        if (e.Swap) Array.Reverse(choices);
        return new { status = "ready", id = e.Id, locale = d.Locale, level = PracticeRules.LevelName(d.Level), cue = d.Cue,
            cueAudioUrl = storage.GeneratePresignedUrl(d.CueAudio, TimeSpan.FromHours(4)), choices,
            pronunciation = PracticePronunciation.Present(e),
            translations = MaoriPracticeTranslation.Present(e),
            translationLocale = MaoriPracticeTranslation.Applies(d.Locale) ? "en-NZ" : null,
            levelStates = PracticeRules.LevelStates(progress), highestUnlockedLevel = PracticeRules.LevelName(PracticeRules.HighestUnlockedLevel(progress)),
            deferredScoring = e.DeferredScoring, translationUsed = e.TranslationUsed,
            currentLevel = PracticeRules.LevelName(PracticeRules.ClientLevel(progress.Level, supportsA0)), practiceLevel = PracticeRules.LevelName(d.Level), reviewMode = savedReview || e.ReviewMode };
    }
    internal static object Feedback(PracticeAttempt a, PracticeExposure e, string transcript) => new {
        outcome = a.Outcome, choice = a.Choice,
        textEquivalent = a.Choice is 0 or 1 && PracticeAnswerMatcher.Normalise(transcript, e.Dialogue.Locale) ==
            PracticeAnswerMatcher.Normalise(a.Choice == (e.Swap ? 1 : 0) ? e.Dialogue.Reply : e.Dialogue.Distractor, e.Dialogue.Locale),
        explanation = a.Outcome == "unclear" ? null : e.Dialogue.Explanation
    };
}

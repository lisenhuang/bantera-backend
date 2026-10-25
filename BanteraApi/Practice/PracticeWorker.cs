using System.Collections.Concurrent;
using System.Threading.Channels;
using BanteraApi.Database;
using BanteraApi.Diagnostics;
using BanteraApi.Gemini;
using BanteraApi.Storage;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Practice;

public sealed class PracticeWorker(IServiceScopeFactory scopes, ILogger<PracticeWorker> logger, AiPipelineEventRecorder events) : BackgroundService
{
    public sealed record Demand(Guid UserId, string Locale, string Language, int Level)
    {
        public Guid RunId { get; } = Guid.NewGuid();
    }
    private readonly PracticeDiagnostics diagnostics = new(events);
    private readonly Channel<Demand> queue = Channel.CreateBounded<Demand>(64);
    private readonly ConcurrentDictionary<string, DateTime> pending = new();
    private readonly SemaphoreSlim audioGate = new(1, 1);
    public void Request(Demand demand)
    {
        var key = $"{demand.Locale}:{demand.Level}";
        var now = DateTime.UtcNow;
        if (pending.TryGetValue(key, out var until) && until > now) return;
        if (pending.Count > 256)
            foreach (var expired in pending.Where(x => x.Value <= now).ToArray()) pending.TryRemove(expired.Key, out _);
        if (!pending.TryAdd(key, now.AddMinutes(15)) &&
            !pending.TryUpdate(key, now.AddMinutes(15), until)) return;
        if (!queue.Writer.TryWrite(demand))
        {
            pending.TryRemove(key, out _);
            logger.LogWarning("Practice queue full for {Locale}/{Level}; run {RunId} can retry on the next demand.", demand.Locale, demand.Level, demand.RunId);
        }
    }
    public void RequestAhead(Guid userId, string locale, string language, PracticeProgress progress, PracticeExposure? active = null, bool useSelected = false, bool supportsA0 = true)
    {
        foreach (var level in PracticePreparation.Levels(progress, active, useSelected || active?.ReviewMode == true, supportsA0)) Request(new(userId, locale, language, PracticeRules.ClientLevel(level, supportsA0)));
    }
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(ConsumeAsync(stoppingToken), ConsumeAsync(stoppingToken));

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var demand in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var failed = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                await diagnostics.RunAsync("pool", new(demand.RunId, demand.Locale, demand.Level), async () =>
                {
                    await FillAsync(demand, timeout.Token);
                    return true;
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { failed = true; logger.LogWarning("Practice batch failed: {Type}; run {RunId}, {Locale}/{Level}. Retrying after 90 seconds on next demand.", ex.GetType().Name, demand.RunId, demand.Locale, demand.Level); }
            finally { pending[$"{demand.Locale}:{demand.Level}"] = DateTime.UtcNow.AddSeconds(failed ? 90 : 5); }
        }
    }
    private async Task FillAsync(Demand demand, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ai = scope.ServiceProvider.GetRequiredService<GeminiService>();
        if (!await db.Users.AnyAsync(x => x.Id == demand.UserId && x.DeletedAt == null, ct)) return;
        await db.Database.OpenConnectionAsync(ct);
        // Transaction-scoped database lock prevents duplicate pool generation across API replicas.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var lockName = "practice-pool:" + demand.Locale + ":" + demand.Level;
        // Another replica may already be filling this pool. Do not block one of
        // our two consumers behind a long remote generation job.
        var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({lockName}, 0)) AS \"Value\"").SingleAsync(ct);
        if (!acquired) return;
        // Commit each ready exercise promptly using a separate scoped context, while holding this lock.
        await using var writeScope = scopes.CreateAsyncScope();
        var writer = writeScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Task<int> ReadyCount() => writer.Set<PracticeDialogue>().CountAsync(d => d.Locale == demand.Locale && d.Level == demand.Level &&
            d.Ready && d.ContentVersion == PracticeRules.ContentVersion &&
            !writer.Set<PracticeExposure>().Any(e => e.UserId == demand.UserId && e.DialogueId == d.Id), ct);
        if (await ReadyCount() >= PracticePreparation.ReadyTarget) return;
        // Resume approved material before paying for more text. A temporary
        // speech-provider failure must not discard completed clips or drafts.
        var unfinished = await writer.Set<PracticeDialogue>().Where(d => d.Locale == demand.Locale && d.Level == demand.Level &&
            !d.Ready && d.ContentVersion == PracticeRules.ContentVersion && PracticeRules.Topics.Contains(d.Topic))
            .OrderBy(d => d.CreatedAt).Take(PracticePreparation.ReadyTarget).ToListAsync(ct);
        foreach (var row in unfinished)
        {
            await CompleteAudioAsync(row, writer, demand.RunId, ct);
            if (await ReadyCount() >= PracticePreparation.ReadyTarget) return;
        }
        // At most two batches per demand; no unbounded regeneration loop.
        for (var batch = 0; batch < 2; batch++)
        {
            if (await ReadyCount() >= PracticePreparation.ReadyTarget) break;
            var history = await writer.Set<PracticeDialogue>().AsNoTracking().Where(d => d.Language == demand.Language && d.ContentVersion == PracticeRules.ContentVersion).ToListAsync(ct);
            var topics = (demand.Level == PracticeRules.BeginnerLevel ? PracticeRules.BeginnerTopics : PracticeRules.Topics).OrderBy(t => history.Count(d => d.Level == demand.Level && d.Topic == t)).Take(5).ToArray();
            var recent = history.OrderByDescending(d => d.CreatedAt).Take(8).Select(d => new { d.Cue, d.Reply, d.MeaningKey }).ToArray();
            // One generation batch and one batch review. No unbounded regeneration loop.
            var logContext = new PracticeLogContext(demand.RunId, demand.Locale, demand.Level, Batch: batch + 1);
            var generated = await diagnostics.RunAsync("text", logContext,
                () => ai.GeneratePracticeAsync(demand.Locale, demand.Level, topics, recent, ct));
            var drafts = generated.Take(5).Where(PracticeRules.Valid).Where(d => PracticePronunciation.Scheme(demand.Locale) == null || PracticePronunciation.Valid(d.Pronunciation, d.Cue, d.Reply, d.Distractor)).Where(d => !history.Any(h => PracticeRules.Duplicate(d, h))).ToArray();
            if (drafts.Length != generated.Length)
                await diagnostics.NoteAsync("text", "candidates_filtered", logContext, new { generated = generated.Length, eligible = drafts.Length });
            if (drafts.Length == 0) return;
            var review = drafts.Select((d, index) => new { index, exercise = d, nearby = history
                .OrderByDescending(h => PracticeRules.Similarity(d.Cue + d.Reply + d.MeaningKey, h.Cue + h.Reply + h.MeaningKey))
                .Take(5).Select(h => new { h.Cue, h.Reply, h.MeaningKey }).ToArray() }).ToArray();
            var approved = (await diagnostics.RunAsync("review", logContext,
                () => ai.ValidatePracticeAsync(demand.Locale, demand.Level, review, ct))).ToHashSet();
            if (approved.Count == 0)
                await diagnostics.NoteAsync("review", "no_candidates_approved", logContext, new { candidates = drafts.Length });
            var reserved = new List<PracticeDialogue>();
            for (var i = 0; i < drafts.Length; i++)
            {
                var draft = drafts[i];
                if (!approved.Contains(i) || history.Any(h => PracticeRules.Duplicate(draft, h))) continue;
                var row = new PracticeDialogue { ContentVersion = PracticeRules.ContentVersion, Locale = demand.Locale, Language = demand.Language, Level = demand.Level,
                    Topic = draft.Topic, MeaningKey = draft.MeaningKey, Cue = draft.Cue, Reply = draft.Reply,
                    Distractor = draft.Distractor, Explanation = draft.Explanation,
                    PronunciationJson = draft.Pronunciation == null ? null : System.Text.Json.JsonSerializer.Serialize(draft.Pronunciation, PracticePronunciation.Json), Fingerprint = PracticeRules.Fingerprint(draft.Cue, draft.Reply) };
                // Reserve the fingerprint before paying for speech synthesis.
                writer.Add(row);
                try { await writer.SaveChangesAsync(ct); }
                catch (DbUpdateException ex)
                {
                    await diagnostics.NoteAsync("save", "draft_reservation_failed", logContext with { DialogueId = row.Id }, PracticeDiagnostics.Describe(ex));
                    writer.Entry(row).State = EntityState.Detached;
                    continue;
                }
                // Keep the approved row even when synthesis needs a retry.
                history.Add(row);
                reserved.Add(row);
            }
            foreach (var row in reserved) await CompleteAudioAsync(row, writer, demand.RunId, ct);
        }
        await transaction.CommitAsync(ct);
    }
    private async Task CompleteAudioAsync(PracticeDialogue row, AppDbContext writer, Guid runId, CancellationToken ct)
    {
        var texts = new[] { row.Cue, row.Reply, row.Distractor };
        await PracticeAudioPreparation.CompleteAsync(row, async part =>
        {
            // Two text pools may prepare concurrently, but speech is bounded
            // globally for this worker to avoid bursts against provider limits.
            await audioGate.WaitAsync(ct);
            try
            {
                await using var clipScope = scopes.CreateAsyncScope();
                var ai = clipScope.ServiceProvider.GetRequiredService<GeminiService>();
                var storage = clipScope.ServiceProvider.GetRequiredService<R2StorageService>();
                var context = new PracticeLogContext(runId, row.Locale, row.Level, row.Id, part);
                var audio = await diagnostics.RunAsync("audio", context,
                    () => ai.GeneratePracticeAudioAsync(texts[part], row.Locale, part != 0, row.Level, ct));
                var key = $"practice/{row.Id:N}/{part}{audio.FileExtension}";
                using var stream = new MemoryStream(audio.Bytes);
                await diagnostics.RunAsync("upload", context, async () =>
                {
                    await storage.UploadObjectAsync(key, stream, audio.ContentType, ct);
                    return true;
                });
                return key;
            }
            finally { audioGate.Release(); }
        }, async () => { await diagnostics.RunAsync("save", new(runId, row.Locale, row.Level, row.Id),
            () => writer.SaveChangesAsync(ct)); });
    }

}

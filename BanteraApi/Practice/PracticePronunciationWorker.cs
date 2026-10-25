using System.Collections.Concurrent;
using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Gemini;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Practice;

/// Durable on-demand queue with bounded retries. Never backfill unrequested exercises.
/// An advisory transaction lock prevents old/new deployment replicas duplicating AI work.
public sealed class PracticePronunciationWorker(IServiceScopeFactory scopes, ILogger<PracticePronunciationWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, byte> requested = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    public void Request(Guid id)
    {
        if (requested.Count < 64) requested.TryAdd(id, 0);
        try { wake.Release(); } catch (SemaphoreFullException) { }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try { worked = await ProcessAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("Practice pronunciation worker unavailable: {Type}", ex.GetType().Name); }
            if (!worked) await wake.WaitAsync(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
    internal static IQueryable<PracticeDialogue> Pending(AppDbContext db, DateTime now) => db.Set<PracticeDialogue>().Where(d => d.Ready && d.ContentVersion == PracticeRules.ContentVersion &&
            d.PronunciationJson == null && d.PronunciationRequestedAt != null && d.PronunciationAttempts < 5 &&
            (d.PronunciationRetryAt == null || d.PronunciationRetryAt <= now) && PracticePronunciation.Locales.Contains(d.Locale));

    private async Task<bool> ProcessAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(stoppingToken);
        var acquired = await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(hashtextextended('practice-pronunciation-v1', 0)) AS \"Value\"").SingleAsync(stoppingToken);
        if (!acquired) return false;
        var now = DateTime.UtcNow;
        var candidates = Pending(db, now);
        var ids = requested.Keys.ToArray();

        var row = ids.Length == 0 ? null : await candidates.Where(d => ids.Contains(d.Id)).OrderBy(d => d.PronunciationRequestedAt).FirstOrDefaultAsync(stoppingToken);
        row ??= await candidates.OrderBy(d => d.PronunciationRequestedAt).FirstOrDefaultAsync(stoppingToken);
        if (row == null) { requested.Clear(); return false; }
        requested.TryRemove(row.Id, out _);
        row.PronunciationAttempts++;
        row.PronunciationRetryAt = now.AddMinutes(15 * row.PronunciationAttempts);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(100));
            var generated = await scope.ServiceProvider.GetRequiredService<GeminiService>().GeneratePracticePronunciationAsync(row, timeout.Token);
            if (!PracticePronunciation.Valid(generated, row.Cue, row.Reply, row.Distractor))
                throw new InvalidDataException("Pronunciation alignment rejected");
            row.PronunciationJson = JsonSerializer.Serialize(generated, PracticePronunciation.Json);
            row.PronunciationRetryAt = null;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Practice pronunciation attempt {Attempt} failed for {DialogueId}: {Type}", row.PronunciationAttempts, row.Id, ex.GetType().Name);
        }
        await db.SaveChangesAsync(stoppingToken);
        await tx.CommitAsync(stoppingToken);
        return true;
    }
}

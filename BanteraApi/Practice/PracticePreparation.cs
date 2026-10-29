namespace BanteraApi.Practice;

/// Prepares possible next levels without scoring or exposing an exercise early.
public static class PracticePreparation
{
    public const int ReadyTarget = 20;
    public const int MaxBatches = 6;
    public const int MaxEmptyBatches = 2;

    // Beginner drills deliberately reuse short patterns with different vocabulary.
    // Exact cue/reply pairs still cannot be repeated, including punctuation variants.
    public static bool Duplicate(PracticeDraft draft, PracticeDialogue old, int level) =>
        level == PracticeRules.BeginnerLevel
            ? PracticeRules.Fingerprint(draft.Cue, draft.Reply) == PracticeRules.Fingerprint(old.Cue, old.Reply)
            : PracticeRules.Duplicate(draft, old);

    public static async Task RefillAsync(Func<Task<int>> readyCount, Func<Task<int>> batch, CancellationToken ct)
    {
        var empty = 0;
        for (var attempt = 0; attempt < MaxBatches; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var before = await readyCount();
            if (before >= ReadyTarget) return;
            var produced = await batch();
            var after = await readyCount();
            if (after >= ReadyTarget) return;
            empty = produced > 0 ? 0 : empty + 1;
            if (empty >= MaxEmptyBatches)
                throw new InvalidOperationException("Practice refill made no progress.");
        }
    }
    public static int[] Levels(PracticeProgress progress, PracticeExposure? active = null, bool useSelected = false, bool supportsA0 = false)
    {
        if (useSelected && PracticeRules.EffectiveLevel(progress) < progress.Level)
            return [PracticeRules.EffectiveLevel(progress)];
        var levels = new HashSet<int> { progress.Level };
        var projected = new PracticeProgress { Level = progress.Level, Window = progress.Window, Language = progress.Language };
        if (active is { DeferredScoring: true, TranslationUsed: false, ReviewMode: false, FinishedAt: null, FirstCorrect: { } correct } &&
            active.Dialogue.Language == progress.Language && active.Dialogue.Level == progress.Level)
            PracticeRules.Record(projected, correct, supportsA0);
        levels.Add(projected.Level);
        // Four scored replies before a possible boundary, warm only reachable
        // neighbouring pools. Translated/unanswered exercises never mutate score.
        if (projected.Window.Length >= 8)
        {
            var states = new List<PracticeProgress> { projected };
            for (var step = 0; step < 4; step++)
            {
                var next = new List<PracticeProgress>();
                foreach (var state in states)
                foreach (var outcome in new[] { false, true })
                {
                    var copy = new PracticeProgress { Level = state.Level, Window = state.Window };
                    PracticeRules.Record(copy, outcome, supportsA0);
                    levels.Add(copy.Level);
                    next.Add(copy);
                }
                states = next;
            }
        }
        return levels.OrderBy(x => x == projected.Level ? 0 : x == progress.Level ? 1 : 2).ThenBy(x => x).ToArray();
    }
}

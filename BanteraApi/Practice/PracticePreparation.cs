namespace BanteraApi.Practice;

/// Prepares possible next levels without scoring or exposing an exercise early.
public static class PracticePreparation
{
    public const int ReadyTarget = 8;
    public static int[] Levels(PracticeProgress progress, PracticeExposure? active = null, bool useSelected = false)
    {
        if (useSelected && PracticeRules.EffectiveLevel(progress) < progress.Level)
            return [PracticeRules.EffectiveLevel(progress)];
        var levels = new HashSet<int> { progress.Level };
        var projected = new PracticeProgress { Level = progress.Level, Window = progress.Window, Language = progress.Language };
        if (active is { DeferredScoring: true, TranslationUsed: false, ReviewMode: false, FinishedAt: null, FirstCorrect: { } correct } &&
            active.Dialogue.Language == progress.Language && active.Dialogue.Level == progress.Level)
            PracticeRules.Record(projected, correct);
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
                    PracticeRules.Record(copy, outcome);
                    levels.Add(copy.Level);
                    next.Add(copy);
                }
                states = next;
            }
        }
        return levels.OrderBy(x => x == projected.Level ? 0 : x == progress.Level ? 1 : 2).ThenBy(x => x).ToArray();
    }
}

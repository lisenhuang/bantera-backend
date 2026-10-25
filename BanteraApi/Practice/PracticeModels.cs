using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BanteraApi.Practice;

public sealed class PracticeDialogue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int ContentVersion { get; set; } = 1;
    public string Locale { get; set; } = "";
    public string Language { get; set; } = "";
    public int Level { get; set; }
    public string Topic { get; set; } = "";
    public string MeaningKey { get; set; } = "";
    public string Cue { get; set; } = "";
    public string Reply { get; set; } = "";
    public string Distractor { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string? PronunciationJson { get; set; }
    public int PronunciationAttempts { get; set; }
    public DateTime? PronunciationRetryAt { get; set; }
    public DateTime? PronunciationRequestedAt { get; set; }
    public string Fingerprint { get; set; } = "";
    public string CueAudio { get; set; } = "";
    public string ReplyAudio { get; set; } = "";
    public string DistractorAudio { get; set; } = "";
    public bool Ready { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public sealed class PracticeExposure
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid RequestId { get; set; }
    public Guid DialogueId { get; set; }
    public PracticeDialogue Dialogue { get; set; } = null!;
    public bool Swap { get; set; }
    public bool? FirstCorrect { get; set; }
    public bool DeferredScoring { get; set; }
    public bool TranslationUsed { get; set; }
    public bool ReviewMode { get; set; }
    public DateTime SeenAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public int Attempts { get; set; }
}
public sealed class PracticeProgress
{
    public Guid UserId { get; set; }
    public string Language { get; set; } = "";
    public int Level { get; set; }
    public string Window { get; set; } = "";
    public int HighestPassedLevel { get; set; } = -2;
    public int? SelectedLevel { get; set; }
}
public sealed class PracticeAttempt
{
    public Guid Id { get; set; }
    public Guid ExposureId { get; set; }
    public string Outcome { get; set; } = "unclear";
    public int? Choice { get; set; }
}
public sealed record PracticeDraft(string Topic, string MeaningKey, string Cue, string Reply, string Distractor, string Explanation, PracticePronunciationData? Pronunciation = null);
public sealed record PracticeJudgement(string Outcome, int? Choice);
public static class PracticeRules
{
    public const int ContentVersion = 2;
    public static readonly string[] Levels = ["A1", "A2", "B1", "B2", "C1", "C2"];
    // Persisted A1-C2 indices remain 0-5; A0 is additive and never renumbers saved data.
    public const int BeginnerLevel = -1;
    public static string LevelName(int level) => level == BeginnerLevel ? "A0" : Levels[level];
    public static int LevelIndex(string level) => level == "A0" ? BeginnerLevel :
        Array.IndexOf(Levels, level) is var index && index >= 0 ? index : -2;
    public static int ClientLevel(int level, bool supportsA0) => supportsA0 ? level : Math.Max(0, level);
    public static readonly string[] BeginnerTopics = ["introductions", "food", "shopping", "home", "help", "weather"];
    public static readonly string[] Topics = ["introductions", "food", "transport", "shopping", "home", "work", "study", "travel", "plans", "help", "appointments", "opinions", "weather", "leisure", "neighbours", "services"];
    public static string Normalise(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    public static string Fingerprint(string cue, string reply) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(cue) + "|" + Normalise(reply))));
    public static double Similarity(string a, string b)
    {
        a = Normalise(a); b = Normalise(b);
        if (a == b) return 1;
        if (a.Length < 3 || b.Length < 3) return 0;
        var left = Enumerable.Range(0, a.Length - 2).Select(i => a.Substring(i, 3)).ToHashSet();
        var right = Enumerable.Range(0, b.Length - 2).Select(i => b.Substring(i, 3)).ToHashSet();
        return 2d * left.Intersect(right).Count() / (left.Count + right.Count);
    }
    public static bool Duplicate(PracticeDraft a, PracticeDialogue b) =>
        Fingerprint(a.Cue, a.Reply) == b.Fingerprint ||
        Normalise(a.MeaningKey) == Normalise(b.MeaningKey) ||
        (Similarity(a.Cue, b.Cue) > .84 && Similarity(a.Reply, b.Reply) > .72);
    public static bool Valid(PracticeDraft d) => Topics.Contains(d.Topic) &&
        new[] { d.Cue, d.Reply, d.Distractor }.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 320) &&
        !string.IsNullOrWhiteSpace(d.MeaningKey) && d.MeaningKey.Length <= 180 &&
        !string.IsNullOrWhiteSpace(d.Explanation) && d.Explanation.Length <= 500 &&
        Normalise(d.Reply) != Normalise(d.Distractor);
    public static int EffectiveLevel(PracticeProgress progress, bool useSelected = true) =>
        useSelected && progress.SelectedLevel is { } selected && selected >= BeginnerLevel && selected < progress.Level
            ? selected : progress.Level;

    // A recommendation to review an easier level never removes an earned pass.
    public static int HighestUnlockedLevel(PracticeProgress progress) =>
        Math.Max(progress.Level, Math.Min(5, progress.HighestPassedLevel + 1));

    public static Dictionary<string, string> LevelStates(PracticeProgress progress) =>
        Enumerable.Range(BeginnerLevel, 7).ToDictionary(LevelName, level =>
            level <= Math.Max(progress.HighestPassedLevel, progress.Level - 1) ? "passed" :
            level <= HighestUnlockedLevel(progress) ? "practicing" : "locked");

    public static bool SelectLevel(PracticeProgress progress, string level)
    {
        var selected = LevelIndex(level);
        if (selected < BeginnerLevel || selected > HighestUnlockedLevel(progress)) return false;
        if (selected > progress.Level)
        {
            progress.Level = selected;
            progress.Window = "";
        }
        progress.SelectedLevel = selected == progress.Level ? null : selected;
        return true;
    }

    public static void Record(PracticeProgress progress, bool correct, bool supportsA0 = false)
    {
        progress.HighestPassedLevel = Math.Max(progress.HighestPassedLevel, progress.Level - 1);
        if (progress.SelectedLevel >= progress.Level) progress.SelectedLevel = null;
        progress.Window += correct ? "1" : "0";
        if (progress.Window.Length < 12) return;
        progress.Window = progress.Window[^12..];
        var successes = progress.Window.Count(c => c == '1');
        if (successes >= 10)
        {
            progress.HighestPassedLevel = Math.Max(progress.HighestPassedLevel, progress.Level);
            if (progress.Level < 5) { progress.Level++; progress.Window = ""; }
        }
        else if (successes <= 4 && progress.Level > (supportsA0 ? BeginnerLevel : 0)) { progress.Level--; progress.Window = ""; }
        if (progress.SelectedLevel >= progress.Level) progress.SelectedLevel = null;
    }

    public static void RecordAnswer(PracticeExposure exposure, PracticeProgress progress, string outcome)
    {
        if (exposure.FinishedAt is not null || outcome == "unclear" || exposure.FirstCorrect is not null) return;
        exposure.FirstCorrect = outcome == "correct";
        if (!exposure.DeferredScoring && !exposure.TranslationUsed && !exposure.ReviewMode && progress.Level == exposure.Dialogue.Level)
            Record(progress, exposure.FirstCorrect.Value);
    }

    public static void Finish(PracticeExposure exposure, PracticeProgress progress, bool supportsA0 = false)
    {
        if (exposure.FinishedAt is not null) return;
        if (exposure.DeferredScoring && !exposure.TranslationUsed && !exposure.ReviewMode && exposure.FirstCorrect is { } correct &&
            exposure.Dialogue.Language == progress.Language && exposure.Dialogue.Level == progress.Level)
            Record(progress, correct, supportsA0);
        exposure.FinishedAt = DateTime.UtcNow;
    }
}

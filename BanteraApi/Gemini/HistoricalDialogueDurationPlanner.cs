using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BanteraApi.Gemini;

public interface IDialogueDurationPlanner
{
    Task<DialogueDurationPlan> CreateAsync(string languageCode, int seconds, string level,
        string audioModel, CancellationToken ct);
}

public sealed record DialogueRateSample(string LanguageCode, string? Level, string Text,
    int DurationMs, string? AudioModel);

/// <summary>
/// Learns from saved AI audio, including existing lessons. Only aggregate rates enter prompts.
/// A missing/unavailable history never prevents generation or causes a regeneration.
/// </summary>
public sealed class HistoricalDialogueDurationPlanner(
    IServiceScopeFactory scopes, IMemoryCache cache, ILogger<HistoricalDialogueDurationPlanner> logger)
    : IDialogueDurationPlanner
{
    public const int MinimumSamples = 3;
    private const int MaximumSamples = 300;

    public async Task<DialogueDurationPlan> CreateAsync(string languageCode, int seconds, string level,
        string audioModel, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var baseline = DialogueDurationPlan.Create(languageCode, seconds, level);
        var locale = NormalizeLocale(languageCode);
        var effectiveLevel = AudioLevels.ForGeneration(level);
        var key = $"dialogue-rate-history:{locale}:{effectiveLevel}";
        if (!cache.TryGetValue(key, out DialogueRateSample[]? samples) || samples is null)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lookup.CancelAfter(TimeSpan.FromSeconds(3));
                samples = await QuerySamples(db, locale, effectiveLevel).ToArrayAsync(lookup.Token);
                cache.Set(key, samples, TimeSpan.FromMinutes(5));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not read audio duration history; using the initial language estimate.");
                cache.Set(key, Array.Empty<DialogueRateSample>(), TimeSpan.FromSeconds(30));
                return baseline;
            }
        }
        return SelectPlan(languageCode, seconds, level, audioModel, samples);
    }

    public static IQueryable<DialogueRateSample> QuerySamples(AppDbContext db, string languageCode, string level)
    {
        var locale = NormalizeLocale(languageCode);
        var family = SpokenLanguage(locale);
        var prefix = family + "-";
        var query = EligibleVideos(db.UserVideos.AsNoTracking(), level);
        // SQL-side family filtering prevents busy languages from crowding out rare locales.
        query = family == "yue"
            ? query.Where(v => v.TranscriptLanguageCode.ToLower().Replace("_", "-") == "zh-hk"
                || v.TranscriptLanguageCode.ToLower().Replace("_", "-") == "yue"
                || v.TranscriptLanguageCode.ToLower().Replace("_", "-").StartsWith("yue-"))
            : query.Where(v => (v.TranscriptLanguageCode.ToLower().Replace("_", "-") == family
                || v.TranscriptLanguageCode.ToLower().Replace("_", "-").StartsWith(prefix))
                && v.TranscriptLanguageCode.ToLower().Replace("_", "-") != "zh-hk");

        return query.OrderByDescending(v => v.TranscriptLanguageCode.ToLower().Replace("_", "-") == locale)
            .ThenByDescending(v => v.CreatedAt).Take(MaximumSamples)
            .Select(v => new DialogueRateSample(v.TranscriptLanguageCode, v.Level, v.TranscriptText,
                v.DurationMs,
                // Older/imported lessons may have no model attribution; retain them as general history.
                (from job in db.UserAudioJobs
                 join ev in db.AiPipelineEvents on job.Id equals ev.JobId
                 where job.VideoId == v.Id && job.Status == "done" && ev.Code == "audio_duration_measured"
                 orderby ev.CreatedAt descending
                 select ev.Model).FirstOrDefault()));
    }

    public static IQueryable<UserVideo> EligibleVideos(IQueryable<UserVideo> query, string level) =>
        AudioLevels.Filter(query, AudioLevels.ForGeneration(level))
            .Where(v => v.IsAiGenerated && !v.IsTranscriptionEstimated
                && v.MediaContentType.StartsWith("audio/") && v.FileSizeBytes > 0
                && v.DurationMs >= 15000 && v.DurationMs <= 1800000
                && v.TranscriptText != "");

    public static DialogueDurationPlan SelectPlan(string languageCode, int seconds, string level,
        string audioModel, IEnumerable<DialogueRateSample> samples)
    {
        var baseline = DialogueDurationPlan.Create(languageCode, seconds, level);
        var locale = NormalizeLocale(languageCode);
        var family = SpokenLanguage(locale);
        var effectiveLevel = AudioLevels.ForGeneration(level);
        var rates = samples
            .Where(s => (AudioLevels.Normalize(s.Level) ?? AudioLevels.Intermediate) == effectiveLevel
                && SpokenLanguage(s.LanguageCode) == family && s.DurationMs is >= 15000 and <= 1800000)
            .Select(s => new
            {
                Locale = NormalizeLocale(s.LanguageCode), s.AudioModel,
                Rate = baseline.CountText(s.Text) * 60000d / s.DurationMs,
            })
            // Broad corruption/silence guards, independent of the old estimated speaking rate.
            .Where(s => s.Rate >= 20 && s.Rate <= (baseline.CountCharacters ? 2000 : 1000))
            .ToArray();

        var exact = rates.Where(s => s.Locale == locale).ToArray();
        var groups = new[]
        {
            (Source: "locale_model_history", Rates: exact.Where(s => s.AudioModel == audioModel).ToArray()),
            (Source: "locale_history", Rates: exact),
            (Source: "language_model_history", Rates: rates.Where(s => s.AudioModel == audioModel).ToArray()),
            (Source: "language_history", Rates: rates),
        };
        foreach (var group in groups)
        {
            if (group.Rates.Length < MinimumSamples) continue;
            var sorted = group.Rates.Select(s => s.Rate).Order().ToArray();
            var middle = sorted.Length / 2;
            var median = sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
            // History already reflects beginner delivery; never apply the beginner reduction twice.
            return baseline with
            {
                UnitsPerMinute = (int)Math.Round(median),
                RateSource = group.Source,
                HistorySampleCount = sorted.Length,
            };
        }
        return baseline;
    }

    private static string NormalizeLocale(string locale) => locale.Trim().Replace('_', '-').ToLowerInvariant();

    private static string SpokenLanguage(string locale)
    {
        var normalized = NormalizeLocale(locale);
        // Bantera's legacy zh-HK locale is Cantonese, not Mandarin.
        return normalized == "zh-hk" ? "yue" : normalized.Split('-')[0];
    }
}

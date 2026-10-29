using BanteraApi.Database;

namespace BanteraApi.Practice;

/// Selection and replenishment share the same account/language/level window.
public static class PracticeRotation
{
    public const int DefaultRecentCount = 50;
    public const int TopicContextLimit = 100;

    public static IQueryable<Guid> RecentIds(IQueryable<PracticeExposure> exposures, Guid userId, string language, int level, int recentCount = DefaultRecentCount) =>
        exposures.Where(e => e.UserId == userId && e.Dialogue.Language == language && e.Dialogue.Level == level)
            .OrderByDescending(e => e.SeenAt).ThenByDescending(e => e.Id)
            .Take(recentCount).Select(e => e.DialogueId);

    public static IQueryable<PracticeDialogue> Available(AppDbContext db, Guid userId, string locale, string language, int level, int recentCount = DefaultRecentCount)
    {
        var recent = RecentIds(db.Set<PracticeExposure>(), userId, language, level, recentCount);
        return db.Set<PracticeDialogue>().Where(d => d.Locale == locale && d.Level == level && d.Ready &&
            d.ContentVersion == PracticeRules.ContentVersion && !recent.Contains(d.Id));
    }

    public sealed record TopicDescription(string Topic, string Situation);
    public static TopicDescription[] Descriptions(IEnumerable<PracticeDialogue> history, string locale, int level, int recentCount = DefaultRecentCount) =>
        history.Where(d => d.Locale == locale && d.Level == level)
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Take(Math.Min(recentCount, TopicContextLimit))
            .Select(d => new TopicDescription(d.Topic, ShortDescription(d.MeaningKey)))
            .Distinct().ToArray();

    private static string ShortDescription(string value)
    {
        var shortText = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(8));
        return string.Concat(shortText.EnumerateRunes().Take(96).Select(r => r.ToString()));
    }
}

using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BanteraApi.Videos;

/// <summary>
/// Admin switch for how AI-audio sentences start. When on, each sentence starts where the
/// previous one ends (just after its last word) instead of at its own first word, so a first
/// word timed a little late ("I") is not clipped. Applied when videos are served, so it takes
/// effect for every AI audio at once and can be switched back.
/// </summary>
public sealed class CueTimingSettingsService(
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    ILogger<CueTimingSettingsService> logger)
{
    public const string StartAtPreviousCueEndKey = "playback.cueStartsAtPreviousCueEnd";
    private const string CacheKey = "cue-timing-settings";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>Cached for 30 s. Read synchronously because responses are built synchronously.</summary>
    public bool StartsAtPreviousCueEnd
    {
        get
        {
            if (cache.TryGetValue(CacheKey, out bool cached))
                return cached;

            var enabled = false;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                enabled = db.AppSettings.AsNoTracking()
                    .Where(s => s.Key == StartAtPreviousCueEndKey)
                    .Select(s => s.Value)
                    .FirstOrDefault() == "true";
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read cue timing settings; using sentence start times as stored.");
            }

            cache.Set(CacheKey, enabled, CacheDuration);
            return enabled;
        }
    }

    public async Task<AppSetting?> GetRowAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == StartAtPreviousCueEndKey, ct);
    }

    public async Task SetAsync(bool enabled, Guid adminUserId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == StartAtPreviousCueEndKey, ct);
        if (row is null)
        {
            row = new AppSetting { Key = StartAtPreviousCueEndKey };
            db.AppSettings.Add(row);
        }
        row.Value = enabled ? "true" : "false";
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByUserId = adminUserId;
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
    }

    /// <summary>
    /// Moves each cue's start back to the previous cue's end (the first cue to 0). Stored cue
    /// ends already sit just after the last word (+150 ms, never past the next line's first
    /// word), so sentences stay back to back: nothing is cut and nothing plays twice.
    /// A start only ever moves earlier, never before the previous cue's own start.
    /// </summary>
    public static IReadOnlyList<VideoTranscriptCue> StartAtPreviousCueEnd(IReadOnlyList<VideoTranscriptCue> cues)
    {
        if (cues.Count == 0) return cues;
        var result = new List<VideoTranscriptCue>(cues.Count);
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var start = i == 0 ? 0 : cues[i - 1].EndMs;
            var floor = i == 0 ? 0 : cues[i - 1].StartMs;
            result.Add(start < cue.StartMs && start >= floor ? cue with { StartMs = start } : cue);
        }
        return result;
    }
}

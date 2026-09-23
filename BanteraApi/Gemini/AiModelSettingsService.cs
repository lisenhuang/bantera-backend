using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BanteraApi.Gemini;

public sealed record AiModelSelection(string TextModel, string AudioModel);

/// <summary>
/// The text and TTS models in use: an admin override from app_settings, else the
/// configured default. Cached briefly so generation does not query the table every call.
/// </summary>
public sealed class AiModelSettingsService(
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    IOptions<GeminiSettings> options,
    ILogger<AiModelSettingsService> logger)
{
    public const string TextModelKey = "ai.textModel";
    public const string AudioModelKey = "ai.audioModel";
    private const string CacheKey = "ai-model-settings";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public AiModelSelection Defaults => new(options.Value.TextModel, options.Value.AudioModel);

    public async Task<AiModelSelection> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out AiModelSelection? cached) && cached is not null)
            return cached;

        var selection = Defaults;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.AppSettings.AsNoTracking()
                .Where(s => s.Key == TextModelKey || s.Key == AudioModelKey)
                .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
            selection = new AiModelSelection(
                rows.GetValueOrDefault(TextModelKey) is { Length: > 0 } text ? text : selection.TextModel,
                rows.GetValueOrDefault(AudioModelKey) is { Length: > 0 } audio ? audio : selection.AudioModel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block generation on the settings table; fall back to config defaults.
            logger.LogWarning(ex, "Could not read AI model settings; using configured defaults.");
        }

        cache.Set(CacheKey, selection, CacheDuration);
        return selection;
    }

    /// <summary>Saves overrides; a null or empty value removes the override (back to the default).</summary>
    public async Task<AiModelSelection> UpdateAsync(string? textModel, string? audioModel, Guid adminUserId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await UpsertAsync(db, TextModelKey, textModel, adminUserId, ct);
        await UpsertAsync(db, AudioModelKey, audioModel, adminUserId, ct);
        await db.SaveChangesAsync(ct);

        cache.Remove(CacheKey);
        return await GetAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, AppSetting>> GetOverridesAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == TextModelKey || s.Key == AudioModelKey)
            .ToDictionaryAsync(s => s.Key, ct);
    }

    private static async Task UpsertAsync(AppDbContext db, string key, string? value, Guid adminUserId, CancellationToken ct)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (row is not null) db.AppSettings.Remove(row);
            return;
        }

        if (row is null)
        {
            row = new AppSetting { Key = key };
            db.AppSettings.Add(row);
        }
        row.Value = value.Trim();
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByUserId = adminUserId;
    }
}

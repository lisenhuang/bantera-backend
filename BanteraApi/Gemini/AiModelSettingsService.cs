using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BanteraApi.Gemini;

public sealed record AiModelSelection(string TextModel, string AudioModel, string? FallbackTextModel, string? FallbackAudioModel, string? TextReasoning = null, string? FallbackTextReasoning = null, string? SearchModel = null, string? FallbackSearchModel = null, string? SearchReasoning = null, string? FallbackSearchReasoning = null, int GptTimeoutSeconds = AiRequestTimeout.DefaultSeconds);

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
    public const string FallbackTextModelKey = "ai.fallbackTextModel";
    public const string FallbackAudioModelKey = "ai.fallbackAudioModel";
    public const string TextReasoningKey = "ai.textReasoning";
    public const string FallbackTextReasoningKey = "ai.fallbackTextReasoning";
    public const string SearchModelKey = "ai.searchModel";
    public const string FallbackSearchModelKey = "ai.fallbackSearchModel";
    public const string SearchReasoningKey = "ai.searchReasoning";
    public const string FallbackSearchReasoningKey = "ai.fallbackSearchReasoning";
    public const string GptTimeoutKey = "ai.gptTimeoutSeconds";
    private const string CacheKey = "ai-model-settings";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public AiModelSelection Defaults => new(options.Value.TextModel, options.Value.AudioModel, null, null, SearchModel: AiSearchPolicy.GeminiModel);

    public async Task<AiModelSelection> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out AiModelSelection? cached) && cached is not null)
            return AiSearchPolicy.Apply(cached);

        var selection = Defaults;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.AppSettings.AsNoTracking()
                .Where(s => s.Key == TextModelKey || s.Key == AudioModelKey ||
                            s.Key == FallbackTextModelKey || s.Key == FallbackAudioModelKey || s.Key == TextReasoningKey || s.Key == FallbackTextReasoningKey || s.Key == SearchModelKey || s.Key == FallbackSearchModelKey || s.Key == SearchReasoningKey || s.Key == FallbackSearchReasoningKey || s.Key == GptTimeoutKey)
                .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
            selection = new AiModelSelection(
                rows.GetValueOrDefault(TextModelKey) is { Length: > 0 } text ? text : selection.TextModel,
                rows.GetValueOrDefault(AudioModelKey) is { Length: > 0 } audio ? audio : selection.AudioModel,
                rows.GetValueOrDefault(FallbackTextModelKey) is { Length: > 0 } fallbackText ? fallbackText : null,
                rows.GetValueOrDefault(FallbackAudioModelKey) is { Length: > 0 } fallbackAudio ? fallbackAudio : null,
                rows.GetValueOrDefault(TextReasoningKey), rows.GetValueOrDefault(FallbackTextReasoningKey),
                rows.GetValueOrDefault(SearchModelKey) ?? selection.SearchModel, rows.GetValueOrDefault(FallbackSearchModelKey),
                rows.GetValueOrDefault(SearchReasoningKey), rows.GetValueOrDefault(FallbackSearchReasoningKey),
                AiRequestTimeout.FromStored(rows.GetValueOrDefault(GptTimeoutKey)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block generation on the settings table; fall back to config defaults.
            logger.LogWarning(ex, "Could not read AI model settings; using configured defaults.");
        }

        selection = AiSearchPolicy.Apply(selection);
        cache.Set(CacheKey, selection, CacheDuration);
        return selection;
    }

    /// <summary>Saves overrides; a null or empty value removes the override (back to the default).</summary>
    public async Task<AiModelSelection> UpdateAsync(
        string? textModel, string? audioModel, Guid adminUserId, CancellationToken ct = default,
        bool updateFallbackTextModel = false, string? fallbackTextModel = null,
        bool updateFallbackAudioModel = false, string? fallbackAudioModel = null,
        bool updateReasoning = false, string? textReasoning = null, string? fallbackTextReasoning = null,
        bool updateSearch = false, string? searchModel = null, string? fallbackSearchModel = null,
        string? searchReasoning = null, string? fallbackSearchReasoning = null,
        bool updateGptTimeout = false, int? gptTimeoutSeconds = null)
    {
        if (updateGptTimeout && gptTimeoutSeconds is int seconds && !AiRequestTimeout.Valid(seconds))
            throw new ArgumentOutOfRangeException(nameof(gptTimeoutSeconds));
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await UpsertAsync(db, TextModelKey, textModel, adminUserId, ct);
        await UpsertAsync(db, AudioModelKey, audioModel, adminUserId, ct);
        if (updateFallbackTextModel)
            await UpsertAsync(db, FallbackTextModelKey, fallbackTextModel, adminUserId, ct);
        if (updateFallbackAudioModel)
            await UpsertAsync(db, FallbackAudioModelKey, fallbackAudioModel, adminUserId, ct);
        if (updateReasoning) {
            await UpsertAsync(db, TextReasoningKey, textReasoning, adminUserId, ct);
            await UpsertAsync(db, FallbackTextReasoningKey, fallbackTextReasoning, adminUserId, ct);
        }
        if (updateSearch) {
            await UpsertAsync(db, SearchModelKey, searchModel, adminUserId, ct);
            await UpsertAsync(db, FallbackSearchModelKey, fallbackSearchModel, adminUserId, ct);
            await UpsertAsync(db, SearchReasoningKey, searchReasoning, adminUserId, ct);
            await UpsertAsync(db, FallbackSearchReasoningKey, fallbackSearchReasoning, adminUserId, ct);
        }
        if (updateGptTimeout)
            await UpsertAsync(db, GptTimeoutKey, gptTimeoutSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture), adminUserId, ct);
        await db.SaveChangesAsync(ct);

        cache.Remove(CacheKey);
        return await GetAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, AppSetting>> GetOverridesAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == TextModelKey || s.Key == AudioModelKey ||
                        s.Key == FallbackTextModelKey || s.Key == FallbackAudioModelKey || s.Key == TextReasoningKey || s.Key == FallbackTextReasoningKey || s.Key == SearchModelKey || s.Key == FallbackSearchModelKey || s.Key == SearchReasoningKey || s.Key == FallbackSearchReasoningKey || s.Key == GptTimeoutKey)
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

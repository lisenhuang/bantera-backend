using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BanteraApi.Gemini;

public enum GeminiKeyFailureKind { Other, QuotaLimited, InvalidKey }

public sealed record GeminiKeyHealthItem(
    string Id, string Hint, string Status, string? Model, DateTimeOffset? RetryAt,
    string? Reason, DateTimeOffset? DetectedAt);

public sealed record GeminiKeyHealthSnapshot(int Total, int Healthy, IReadOnlyList<GeminiKeyHealthItem> Items);

/// <summary>
/// Keeps short, model-specific quota cooldowns in memory and confirmed invalid keys in
/// app_settings so they remain disabled after a restart. Only key hashes and masked hints
/// are stored; the configured API keys remain in server environment variables.
/// </summary>
public sealed class GeminiKeyHealthService(
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    IOptions<GeminiSettings> options,
    ILogger<GeminiKeyHealthService> logger)
{
    private const string InvalidPrefix = "ai.gemini.invalidKey.";
    private const string InvalidCacheKey = "gemini-invalid-keys";
    private static readonly TimeSpan InvalidCacheDuration = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, Cooldown> cooldowns = new();
    private readonly ConcurrentDictionary<string, InvalidState> recentlyInvalid = new();

    private sealed record Cooldown(DateTimeOffset Until, int Strikes);
    private sealed record InvalidState(string Hint, string Reason, DateTimeOffset MarkedAt);

    public static string KeyId(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public static string MaskKey(string key) => key.Length <= 10 ? "***" : $"{key[..6]}…{key[^4..]}";

    public static GeminiKeyFailureKind Classify(Exception error)
    {
        if (error is not HttpRequestException httpError) return GeminiKeyFailureKind.Other;
        if (httpError.StatusCode == HttpStatusCode.TooManyRequests) return GeminiKeyFailureKind.QuotaLimited;

        // GenerateContent can report an invalid key as HTTP 400 with API_KEY_INVALID.
        // A generic 403 may instead be specific to one model, so do not disable it.
        var message = httpError.Message;
        if (httpError.StatusCode == HttpStatusCode.Unauthorized ||
            message.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("API key was reported as leaked", StringComparison.OrdinalIgnoreCase) ||
            (message.Contains("API key", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("revoked", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("expired", StringComparison.OrdinalIgnoreCase))))
            return GeminiKeyFailureKind.InvalidKey;
        return GeminiKeyFailureKind.Other;
    }

    public async Task<string[]> EligibleKeysAsync(string[] keys, string? model, CancellationToken ct)
    {
        var invalid = await GetInvalidAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return keys.Where(key => !invalid.ContainsKey(KeyId(key)) &&
            !recentlyInvalid.ContainsKey(KeyId(key)) &&
            !(cooldowns.TryGetValue(CooldownId(KeyId(key), model), out var cooldown) && cooldown.Until > now))
            .ToArray();
    }

    public DateTimeOffset CoolDown(string key, string? model)
    {
        var now = DateTimeOffset.UtcNow;
        var cooldown = cooldowns.AddOrUpdate(CooldownId(KeyId(key), model),
            _ => new Cooldown(now.AddMinutes(2), 1),
            (_, previous) =>
            {
                if (previous.Until < now.AddMinutes(-10))
                    return new Cooldown(now.AddMinutes(2), 1);
                var strikes = Math.Min(previous.Strikes + 1, 5);
                return new Cooldown(now.AddMinutes(Math.Min(30, 1 << strikes)), strikes);
            });
        return cooldown.Until;
    }

    public async Task MarkInvalidAsync(string key, string reason, CancellationToken ct)
    {
        var id = KeyId(key);
        var state = new InvalidState(MaskKey(key), reason, DateTimeOffset.UtcNow);
        recentlyInvalid[id] = state;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settingKey = InvalidPrefix + id;
            var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == settingKey, ct);
            if (row is null)
            {
                row = new AppSetting { Key = settingKey };
                db.AppSettings.Add(row);
            }
            row.Value = JsonSerializer.Serialize(state);
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            cache.Remove(InvalidCacheKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not persist invalid Gemini key {KeyHint}.", state.Hint);
        }
    }

    public async Task<GeminiKeyHealthSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var invalid = await GetInvalidAsync(ct);
        var keys = GeminiService.SelectKeys(options.Value.ApiKeys, false, options.Value.WebSearchKeyPrefix);
        var now = DateTimeOffset.UtcNow;
        var items = new List<GeminiKeyHealthItem>();
        var affected = 0;
        foreach (var key in keys)
        {
            var id = KeyId(key);
            var hint = MaskKey(key);
            var state = recentlyInvalid.GetValueOrDefault(id) ?? invalid.GetValueOrDefault(id);
            if (state is not null)
            {
                items.Add(new GeminiKeyHealthItem(id, hint, "invalid", null, null, state.Reason, state.MarkedAt));
                affected++;
                continue;
            }
            var activeCooldowns = cooldowns
                .Where(entry => entry.Key.StartsWith(id + "|", StringComparison.Ordinal) && entry.Value.Until > now)
                .ToArray();
            if (activeCooldowns.Length == 0) continue;
            affected++;
            foreach (var entry in activeCooldowns)
            {
                var model = entry.Key[(id.Length + 1)..];
                items.Add(new GeminiKeyHealthItem(id, hint, "cooldown", model == "*" ? null : model,
                    entry.Value.Until, "Quota limit (HTTP 429)", null));
            }
        }
        return new GeminiKeyHealthSnapshot(keys.Length, keys.Length - affected,
            items.OrderBy(i => i.Status == "invalid" ? 0 : 1).ThenBy(i => i.Hint).ToArray());
    }

    public async Task<bool> ReenableAsync(string id, CancellationToken ct)
    {
        var keys = GeminiService.SelectKeys(options.Value.ApiKeys, false, options.Value.WebSearchKeyPrefix);
        if (!keys.Any(key => KeyId(key) == id)) return false;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == InvalidPrefix + id, ct);
        if (row is not null)
        {
            db.AppSettings.Remove(row);
            await db.SaveChangesAsync(ct);
        }
        recentlyInvalid.TryRemove(id, out _);
        cache.Remove(InvalidCacheKey);
        return true;
    }

    private async Task<IReadOnlyDictionary<string, InvalidState>> GetInvalidAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(InvalidCacheKey, out Dictionary<string, InvalidState>? cached) && cached is not null)
            return cached;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.AppSettings.AsNoTracking()
                .Where(x => x.Key.StartsWith(InvalidPrefix))
                .Select(x => new { x.Key, x.Value })
                .ToListAsync(ct);
            var result = new Dictionary<string, InvalidState>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                try
                {
                    if (JsonSerializer.Deserialize<InvalidState>(row.Value) is { } state)
                        result[row.Key[InvalidPrefix.Length..]] = state;
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Could not read Gemini key health setting {SettingKey}.", row.Key);
                }
            }
            cache.Set(InvalidCacheKey, result, InvalidCacheDuration);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not load invalid Gemini keys; using current process state.");
            return recentlyInvalid;
        }
    }

    private static string CooldownId(string id, string? model) => id + "|" + (model ?? "*");
}

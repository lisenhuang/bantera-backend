using System.Text;
using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.OpenAi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BanteraApi.Gemini;

public sealed record RegularKeyEntry(string Id, string? Key, string Remarks, bool Enabled = true);
public sealed record RegularKeyItem(string Id, string Hint, string Source, string Remarks, bool Enabled);
public sealed record RegularKeyStatus(bool StorageReady, RegularKeyItem[] Items);

// Environment-key metadata and dashboard keys share one encrypted setting. Secrets never leave this service.
public class GeminiRegularKeyStore(IServiceScopeFactory scopes, IOptions<GeminiSettings> settings,
    IOptions<ChatGptConnectionOptions> encryption, GeminiPaidFallback paidFallback)
{
    internal const string Setting = "ai.gemini.regularKeys.v1";
    public bool StorageReady => paidFallback.StorageReady;
    private string[] EnvironmentKeys => settings.Value.ApiKeys.Where(k => !string.IsNullOrWhiteSpace(k))
        .Select(k => k.Trim()).Distinct(StringComparer.Ordinal).ToArray();

    internal static string Encode(RegularKeyEntry[] entries, string secret) => Convert.ToBase64String(
        ChatGptConnection.Seal(JsonSerializer.SerializeToUtf8Bytes(entries), Setting, Convert.FromBase64String(secret)));
    internal static RegularKeyEntry[] Decode(string value, string secret) =>
        JsonSerializer.Deserialize<RegularKeyEntry[]>(ChatGptConnection.Unseal(Encoding.UTF8.GetBytes(value), Setting, Convert.FromBase64String(secret)))
            ?? throw new InvalidDataException("Regular key configuration unavailable.");

    internal async Task<RegularKeyEntry[]> ReadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var value = await db.AppSettings.AsNoTracking().Where(s => s.Key == Setting).Select(s => s.Value).SingleOrDefaultAsync(ct);
        return value is null ? [] : Decode(value, encryption.Value.EncryptionKey);
    }

    internal static string[] Merge(IEnumerable<string> environment, RegularKeyEntry[] entries, bool includeDisabled = false) =>
        environment.Concat(entries.Where(e => e.Key is not null).Select(e => e.Key!))
            .Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.Ordinal)
            .Where(k => includeDisabled || entries.All(e => e.Id != GeminiKeyHealthService.KeyId(k) || e.Enabled)).ToArray();

    public virtual async Task<string[]> KeysAsync(CancellationToken ct, bool includeDisabled = false) =>
        Merge(EnvironmentKeys, await ReadAsync(ct), includeDisabled);

    public async Task<RegularKeyStatus> StatusAsync(CancellationToken ct)
    {
        var entries = await ReadAsync(ct);
        var environment = EnvironmentKeys.ToHashSet(StringComparer.Ordinal);
        var paid = (await paidFallback.ReadAsync(ct)).Key;
        var items = Merge(environment, entries, true).Where(k => k != paid).Select(key => {
            var id = GeminiKeyHealthService.KeyId(key);
            var entry = entries.FirstOrDefault(e => e.Id == id);
            return new RegularKeyItem(id, GeminiKeyHealthService.MaskKey(key), environment.Contains(key) ? "environment" : "dashboard",
                entry?.Remarks ?? "", entry?.Enabled ?? true);
        }).OrderBy(i => i.Source).ThenBy(i => i.Id).ToArray();
        return new(StorageReady, items);
    }

    public Task AddAsync(string key, string remarks, Guid admin, CancellationToken ct) => MutateAsync(entries => {
        if (Merge(EnvironmentKeys, entries, true).Contains(key, StringComparer.Ordinal)) throw new ArgumentException("duplicate_key");
        if (entries.Count(e => e.Key is not null) >= 200) throw new ArgumentException("pool_full");
        var id = GeminiKeyHealthService.KeyId(key);
        // Replace stale metadata when a former environment key is added through the dashboard.
        return [.. entries.Where(e => e.Id != id), new(id, key, remarks)];
    }, admin, ct, key);

    public Task UpdateAsync(string id, string remarks, bool enabled, Guid admin, CancellationToken ct) => MutateAsync(entries => {
        if (!Merge(EnvironmentKeys, entries, true).Any(k => GeminiKeyHealthService.KeyId(k) == id)) throw new ArgumentException("key_not_found");
        var previous = entries.FirstOrDefault(e => e.Id == id);
        return [.. entries.Where(e => e.Id != id), new(id, previous?.Key, remarks, enabled)];
    }, admin, ct);

    public Task RemoveAsync(string id, Guid admin, CancellationToken ct) => MutateAsync(entries => {
        if (EnvironmentKeys.Any(k => GeminiKeyHealthService.KeyId(k) == id)) throw new ArgumentException("environment_key");
        if (!entries.Any(e => e.Id == id && e.Key is not null)) throw new ArgumentException("key_not_found");
        return entries.Where(e => e.Id != id).ToArray();
    }, admin, ct);

    private async Task MutateAsync(Func<RegularKeyEntry[], RegularKeyEntry[]> change, Guid admin, CancellationToken ct, string? addedKey = null)
    {
        if (!StorageReady) throw new InvalidOperationException("Secure key storage unavailable.");
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Use the paid-setting lock too: simultaneous edits cannot classify the same key differently.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(626170616964)", ct);
        if (addedKey is not null && addedKey == (await paidFallback.ReadAsync(ct)).Key) throw new ArgumentException("paid_key");
        var row = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == Setting, ct);
        var entries = row is null ? [] : Decode(row.Value, encryption.Value.EncryptionKey);
        var updated = change(entries);
        if (row is null) { row = new AppSetting { Key = Setting }; db.AppSettings.Add(row); }
        row.Value = Encode(updated, encryption.Value.EncryptionKey);
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByUserId = admin;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

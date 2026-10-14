using System.Text;
using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.OpenAi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BanteraApi.Gemini;

public sealed record PaidFallbackPolicy(string? Key, Guid[] AllowedUserIds, Guid[]? PaidOnlyUserIds = null, Guid[]? RegularOnlyUserIds = null);
public sealed record PaidFallbackUser(Guid Id, string? Name, string? Email, string Mode = "fallback");
public sealed record PaidKeyPreference(bool CanConfigure, string Mode);
public sealed record PaidFallbackStatus(bool StorageReady, bool Configured, string? Hint, PaidFallbackUser[] Users);

// One encrypted atomic setting. No key is ever returned by the admin API.
public class GeminiPaidFallback(IServiceScopeFactory scopes, IOptions<ChatGptConnectionOptions> encryption)
{
    internal const string Setting = "ai.gemini.paidFallback.v1";
    public bool StorageReady {
        get { try { return Convert.FromBase64String(encryption.Value.EncryptionKey).Length == 32; } catch { return false; } }
    }
    public virtual async Task<PaidFallbackPolicy> ReadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var value = await db.AppSettings.AsNoTracking().Where(s => s.Key == Setting).Select(s => s.Value).SingleOrDefaultAsync(ct);
        if (value is null) return new(null, []);
        // Fail closed on unreadable configuration; never guess a key's priority or access.
        return Decode(value, encryption.Value.EncryptionKey);
    }
    internal static PaidFallbackPolicy Decode(string value, string secret) =>
        JsonSerializer.Deserialize<PaidFallbackPolicy>(ChatGptConnection.Unseal(Encoding.UTF8.GetBytes(value), Setting, Convert.FromBase64String(secret)))
            ?? throw new InvalidDataException("Paid fallback configuration unavailable.");
    internal static string Encode(PaidFallbackPolicy policy, string secret) =>
        Convert.ToBase64String(ChatGptConnection.Seal(JsonSerializer.SerializeToUtf8Bytes(policy), Setting, Convert.FromBase64String(secret)));
    public async Task SaveAsync(string? key, bool removeKey, Guid[] users, Guid[] paidOnly, Guid admin, CancellationToken ct, Guid[]? regularOnly = null)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize admin and per-user edits so neither overwrites a concurrent change.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(626170616964)", ct);
        var previous = await ReadAsync(ct);
        var selected = removeKey ? null : string.IsNullOrWhiteSpace(key) ? previous.Key : key.Trim();
        var ids = users.Distinct().ToArray();
        var regular = regularOnly ?? (previous.RegularOnlyUserIds ?? []).Intersect(ids).Except(paidOnly).ToArray();
        if (regular.Except(ids).Any() || regular.Intersect(paidOnly).Any()) throw new ArgumentException("Choose one mode for each selected user.");
        if (paidOnly.Except(ids).Any()) throw new ArgumentException("Paid-only users must be selected.");
        if (await db.Users.CountAsync(u => ids.Contains(u.Id) && u.DeletedAt == null && u.Status == "active", ct) != ids.Length)
            throw new ArgumentException("Choose active users only.");
        var row = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == Setting, ct);
        if (row is null) { row = new AppSetting { Key = Setting }; db.AppSettings.Add(row); }
        row.Value = Encode(new(selected, ids, paidOnly.Distinct().ToArray(), regular.Distinct().ToArray()), encryption.Value.EncryptionKey);
        row.UpdatedAt = DateTime.UtcNow; row.UpdatedByUserId = admin;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    public async Task<PaidFallbackStatus> StatusAsync(CancellationToken ct)
    {
        var policy = await ReadAsync(ct);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = await db.Users.AsNoTracking().Where(u => policy.AllowedUserIds.Contains(u.Id) && u.DeletedAt == null)
            .Select(u => new PaidFallbackUser(u.Id, u.Name, u.Identities.Select(i => i.ProviderEmail).FirstOrDefault(), "fallback")).ToArrayAsync(ct);
        return new(StorageReady, policy.Key is not null, policy.Key is null ? null : GeminiKeyHealthService.MaskKey(policy.Key), users.Select(u => u with { Mode = Mode(policy, u.Id) }).ToArray());
    }
    public virtual async Task<PaidFallbackPolicy> ForUserAsync(Guid? user, CancellationToken ct)
    {
        var policy = await ReadAsync(ct);
        if (user is null || !policy.AllowedUserIds.Contains(user.Value) || policy.RegularOnlyUserIds?.Contains(user.Value) == true) return policy with { AllowedUserIds = [] };
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AnyAsync(u => u.Id == user && u.Status == "active" && u.DeletedAt == null, ct)
            ? policy : policy with { AllowedUserIds = [] };
    }
    internal static string Mode(PaidFallbackPolicy policy, Guid user) => policy.RegularOnlyUserIds?.Contains(user) == true
        ? "regular_only" : policy.PaidOnlyUserIds?.Contains(user) == true ? "paid_only" : "fallback";
    public async Task<PaidKeyPreference> PreferenceAsync(Guid user, CancellationToken ct)
    {
        var policy = await ReadAsync(ct);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var allowed = policy.Key is not null && policy.AllowedUserIds.Contains(user) &&
            await db.Users.AnyAsync(u => u.Id == user && u.Status == "active" && u.DeletedAt == null, ct);
        return new(allowed, allowed ? Mode(policy, user) : "regular_only");
    }
    public async Task<bool> SavePreferenceAsync(Guid user, string mode, CancellationToken ct)
    {
        if (mode is not ("regular_only" or "fallback" or "paid_only")) throw new ArgumentException("Invalid mode.");
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(626170616964)", ct);
        var row = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == Setting, ct);
        if (row is null) return false;
        var policy = Decode(row.Value, encryption.Value.EncryptionKey);
        if (policy.Key is null || !policy.AllowedUserIds.Contains(user) ||
            !await db.Users.AnyAsync(u => u.Id == user && u.Status == "active" && u.DeletedAt == null, ct)) return false;
        policy = policy with {
            PaidOnlyUserIds = (policy.PaidOnlyUserIds ?? []).Where(id => id != user).Concat(mode == "paid_only" ? new[] { user } : []).ToArray(),
            RegularOnlyUserIds = (policy.RegularOnlyUserIds ?? []).Where(id => id != user).Concat(mode == "regular_only" ? new[] { user } : []).ToArray()
        };
        row.Value = Encode(policy, encryption.Value.EncryptionKey);
        row.UpdatedAt = DateTime.UtcNow; row.UpdatedByUserId = user;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
    internal static string[] Order(IEnumerable<string> regular, PaidFallbackPolicy policy, Guid? user, string? preferredHash = null, bool webSearch = false)
    {
        if (user is { } selected && policy.RegularOnlyUserIds?.Contains(selected) == true) policy = policy with { AllowedUserIds = [] };
        // Exclude a duplicate paid key from the regular pool even for denied users.
        var paidOnly = user is { } uid && policy.AllowedUserIds.Contains(uid) && policy.PaidOnlyUserIds?.Contains(uid) == true;
        var keys = (paidOnly ? Array.Empty<string>() : regular).Where(k => k != policy.Key).Distinct(StringComparer.Ordinal)
            .OrderByDescending(k => string.Equals(GeminiKeyHealthService.KeyId(k), preferredHash, StringComparison.OrdinalIgnoreCase)).ToList();
        if (policy.Key is { } paid && user is { } id && policy.AllowedUserIds.Contains(id) &&
            (!webSearch || paid.StartsWith(AiSearchPolicy.GeminiKeyPrefix, StringComparison.Ordinal))) keys.Add(paid);
        return keys.ToArray();
    }
}

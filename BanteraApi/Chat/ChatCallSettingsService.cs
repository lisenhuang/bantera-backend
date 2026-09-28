using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Chat;

public sealed class ChatCallSettingsService(AppDbContext db)
{
    public const string SettingKey = "chat.iceTransportPolicy";
    public static bool IsValidPolicy(string? policy) => policy is "all" or "relay";
    public static string ResolvePolicy(string? value) => value == "relay" ? "relay" : "all";

    public Task<AppSetting?> GetRowAsync(CancellationToken ct = default) =>
        db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKey, ct);

    public async Task<string> GetPolicyAsync(CancellationToken ct = default) =>
        ResolvePolicy((await GetRowAsync(ct))?.Value);

    public async Task SetAsync(string policy, Guid adminId, CancellationToken ct = default)
    {
        if (!IsValidPolicy(policy)) throw new ArgumentException("Invalid call connection mode.", nameof(policy));
        var now = DateTime.UtcNow;
        // Atomic upsert also handles simultaneous first saves by different admins.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app_settings ("Key", "Value", "UpdatedAt", "UpdatedByUserId")
            VALUES ({SettingKey}, {policy}, {now}, {adminId})
            ON CONFLICT ("Key") DO UPDATE SET
                "Value" = EXCLUDED."Value", "UpdatedAt" = EXCLUDED."UpdatedAt",
                "UpdatedByUserId" = EXCLUDED."UpdatedByUserId"
            """, ct);
    }
}

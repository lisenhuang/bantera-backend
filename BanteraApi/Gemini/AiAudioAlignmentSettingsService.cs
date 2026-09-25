using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Gemini;

/// <summary>Controls whether newly generated audio uses script-aligned or verbatim transcript timing.</summary>
public sealed class AiAudioAlignmentSettingsService(AppDbContext db)
{
    public const string AlignToOriginalDialogueKey = "aiAudio.alignToOriginalDialogue";

    public async Task<AppSetting?> GetRowAsync(CancellationToken ct = default) =>
        await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == AlignToOriginalDialogueKey, ct);

    public async Task<bool> GetAsync(CancellationToken ct = default) =>
        (await GetRowAsync(ct))?.Value != "false";

    public async Task SetAsync(bool enabled, Guid adminUserId, CancellationToken ct = default)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == AlignToOriginalDialogueKey, ct);
        if (row is null)
        {
            row = new AppSetting { Key = AlignToOriginalDialogueKey };
            db.AppSettings.Add(row);
        }
        row.Value = enabled ? "true" : "false";
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByUserId = adminUserId;
        await db.SaveChangesAsync(ct);
    }
}

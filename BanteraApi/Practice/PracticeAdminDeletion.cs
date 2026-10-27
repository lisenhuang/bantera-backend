using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Practice;

public static class PracticeAdminDeletion
{
    // An exposure identifies the currently displayed practice. Deleting its source
    // also removes linked exposures/attempts via existing foreign keys; earned
    // progress and word totals are separate and remain intact.
    public static async Task<PracticeDialogue?> DeleteAsync(AppDbContext db, Guid owner, Guid exposureId, CancellationToken ct)
    {
        var dialogue = await db.Set<PracticeExposure>().AsNoTracking()
            .Where(e => e.Id == exposureId && e.UserId == owner)
            .Select(e => e.Dialogue).FirstOrDefaultAsync(ct);
        if (dialogue == null) return null;
        var deleted = await db.Set<PracticeDialogue>().Where(d => d.Id == dialogue.Id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? null : dialogue;
    }
}

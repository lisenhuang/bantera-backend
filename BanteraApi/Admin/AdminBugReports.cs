using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Admin;

public sealed record AdminBugReport(Guid Id, Guid UserId, string? UserName, string Summary,
    string Details, string Category, string Source, string ConsentQuote, DateTime CreatedAt);
public static class AdminBugReports
{
    public static async Task<AdminPagedResult<AdminBugReport>> ListAsync(AppDbContext db, int limit, int offset, CancellationToken ct)
    {
        var query = db.AiBugReports.AsNoTracking();
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 100))
            .Select(x => new AdminBugReport(x.Id, x.UserId, x.User.Name, x.Summary, x.Details,
                x.Category, x.Source, x.ConsentQuote, x.CreatedAt)).ToListAsync(ct);
        return new(items, total);
    }
}

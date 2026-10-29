using System.Security.Claims;
using System.Globalization;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Practice;

public static class PracticeSettings
{
    public const string Key = "practice.repeatWindow";
    public static bool Valid(int count) => count is >= 10 and <= 500;
    public static int Resolve(string? value) => int.TryParse(value, out var count) && Valid(count)
        ? count : PracticeRotation.DefaultRecentCount;
    public static async Task<int> GetAsync(AppDbContext db, CancellationToken ct) =>
        Resolve(await db.AppSettings.AsNoTracking().Where(s => s.Key == Key).Select(s => s.Value).SingleOrDefaultAsync(ct));
    public static async Task SaveAsync(AppDbContext db, int count, Guid admin, CancellationToken ct)
    {
        if (!Valid(count)) throw new ArgumentOutOfRangeException(nameof(count));
        var value = count.ToString(CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app_settings ("Key", "Value", "UpdatedAt", "UpdatedByUserId")
            VALUES ({Key}, {value}, {now}, {admin})
            ON CONFLICT ("Key") DO UPDATE SET "Value" = EXCLUDED."Value",
                "UpdatedAt" = EXCLUDED."UpdatedAt", "UpdatedByUserId" = EXCLUDED."UpdatedByUserId"
            """, ct);
    }
    public sealed record UpdateRequest(int RepeatWindow);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/practice-settings").RequireAuthorization("Admin");
        group.MapGet("", async (AppDbContext db, HttpContext http, CancellationToken ct) => {
            http.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(new { repeatWindow = await GetAsync(db, ct) });
        });
        group.MapPut("", async (UpdateRequest request, AppDbContext db, ClaimsPrincipal user, CancellationToken ct) => {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var admin)) return Results.Unauthorized();
            if (!Valid(request.RepeatWindow)) return Results.BadRequest(new { message = "Choose a whole number from 10 to 500." });
            await SaveAsync(db, request.RepeatWindow, admin, ct);
            return Results.Ok(new { repeatWindow = request.RepeatWindow });
        });
    }
}

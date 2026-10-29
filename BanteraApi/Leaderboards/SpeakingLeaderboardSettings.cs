using System.Globalization;
using System.Security.Claims;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Leaderboards;

public static class SpeakingLeaderboardSettings
{
    public const string Key = "speakingLeaderboard.days";
    public const int DefaultDays = 30;
    public static bool Valid(int days) => days is >= 1 and <= 365;
    public static int RetentionDays(int days) => Valid(days) ? Math.Max(35, days + 5) : 35;
    public static int Resolve(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && Valid(days)
        ? days : DefaultDays;
    public static async Task<int> GetAsync(AppDbContext db, CancellationToken ct) =>
        Resolve(await db.AppSettings.AsNoTracking().Where(s => s.Key == Key).Select(s => s.Value).SingleOrDefaultAsync(ct));
    public sealed record UpdateRequest(int Days);

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/speaking-leaderboard-settings").RequireAuthorization("Admin");
        group.MapGet("", async (AppDbContext db, HttpContext context, CancellationToken ct) => {
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(new { days = await GetAsync(db, ct) });
        });
        group.MapPut("", async (UpdateRequest request, AppDbContext db, ClaimsPrincipal user, HttpContext context, CancellationToken ct) => {
            if (!Guid.TryParse(user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier), out var admin)) return Results.Unauthorized();
            if (!Valid(request.Days)) return Results.BadRequest(new { message = "Choose a whole number from 1 to 365." });
            var value = request.Days.ToString(CultureInfo.InvariantCulture);
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO app_settings ("Key", "Value", "UpdatedAt", "UpdatedByUserId")
                VALUES ({Key}, {value}, {now}, {admin})
                ON CONFLICT ("Key") DO UPDATE SET "Value" = EXCLUDED."Value",
                    "UpdatedAt" = EXCLUDED."UpdatedAt", "UpdatedByUserId" = EXCLUDED."UpdatedByUserId"
                """, ct);
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(new { days = request.Days });
        });
    }
}

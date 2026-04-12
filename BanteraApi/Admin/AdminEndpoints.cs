using BanteraApi.Auth;
using Microsoft.AspNetCore.Mvc;

namespace BanteraApi.Admin;

public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        // GET /api/admin/stats
        group.MapGet("/stats", async (AdminService admin) =>
        {
            var stats = await admin.GetStatsAsync();
            return Results.Ok(stats);
        })
        .WithName("AdminGetStats");

        // GET /api/admin/users
        group.MapGet("/users", async (
            AdminService admin,
            [FromQuery] string? search,
            [FromQuery] string? sort,
            [FromQuery] string? dir,
            [FromQuery] int limit = 20,
            [FromQuery] int offset = 0) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            var result = await admin.ListUsersAsync(search, sort, dir, limit, offset);
            return Results.Ok(result);
        })
        .WithName("AdminListUsers");

        // GET /api/admin/users/{userId}
        group.MapGet("/users/{userId:guid}", async (Guid userId, AdminService admin) =>
        {
            var user = await admin.GetUserDetailAsync(userId);
            return user is null
                ? Results.NotFound(new ApiError("user_not_found", "User not found."))
                : Results.Ok(user);
        })
        .WithName("AdminGetUser");

        // PATCH /api/admin/users/{userId}
        group.MapPatch("/users/{userId:guid}", async (
            Guid userId,
            PatchUserRequest req,
            AdminService admin) =>
        {
            var ok = await admin.PatchUserAsync(userId, req.Role, req.Status, req.AiAudioDailyLimit, req.ClearAiLimit);
            return ok
                ? Results.Ok(new { updated = true })
                : Results.NotFound(new ApiError("user_not_found", "User not found."));
        })
        .WithName("AdminPatchUser");

        // DELETE /api/admin/users/{userId}
        group.MapDelete("/users/{userId:guid}", async (Guid userId, AdminService admin) =>
        {
            var ok = await admin.DeleteUserAsync(userId);
            return ok
                ? Results.Ok(new { deleted = true })
                : Results.NotFound(new ApiError("user_not_found", "User not found."));
        })
        .WithName("AdminDeleteUser");

        // GET /api/admin/videos
        group.MapGet("/videos", async (
            AdminService admin,
            [FromQuery] string? languageCode,
            [FromQuery] bool? isPublic,
            [FromQuery] bool? isAiGenerated,
            [FromQuery] string? sort,
            [FromQuery] string? dir,
            [FromQuery] int limit = 20,
            [FromQuery] int offset = 0) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            var result = await admin.ListVideosAsync(languageCode, isPublic, isAiGenerated, sort, dir, limit, offset);
            return Results.Ok(result);
        })
        .WithName("AdminListVideos");

        // DELETE /api/admin/videos/{videoId}
        group.MapDelete("/videos/{videoId:guid}", async (Guid videoId, AdminService admin) =>
        {
            var ok = await admin.DeleteVideoAsync(videoId);
            return ok
                ? Results.Ok(new { deleted = true })
                : Results.NotFound(new ApiError("video_not_found", "Video not found."));
        })
        .WithName("AdminDeleteVideo");
    }
}

public record PatchUserRequest(
    string? Role,
    string? Status,
    int? AiAudioDailyLimit,
    bool ClearAiLimit = false);

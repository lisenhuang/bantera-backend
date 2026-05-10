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

        // GET /api/admin/messages
        group.MapGet("/messages", async (
            AdminService admin,
            [FromQuery] string? threadType,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] int limit = 20,
            [FromQuery] int offset = 0) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            var result = await admin.ListChatMessagesAsync(threadType, from, to, limit, offset);
            return Results.Ok(result);
        })
        .WithName("AdminListMessages");

        // GET /api/admin/messages/{messageId}/audio
        group.MapGet("/messages/{messageId:guid}/audio", async (
            Guid messageId,
            AdminService admin,
            CancellationToken ct) =>
        {
            var audio = await admin.GetChatMessageAudioAsync(messageId, ct);
            return audio is null
                ? Results.NotFound(new ApiError("message_not_found", "Message not found."))
                : Results.Stream(audio.Stream, audio.ContentType, enableRangeProcessing: true);
        })
        .WithName("AdminGetMessageAudio");

        // DELETE /api/admin/messages/{messageId}
        group.MapDelete("/messages/{messageId:guid}", async (
            Guid messageId,
            AdminService admin,
            CancellationToken ct) =>
        {
            var ok = await admin.DeleteChatMessageAsync(messageId, ct);
            return ok
                ? Results.NoContent()
                : Results.NotFound(new ApiError("message_not_found", "Message not found."));
        })
        .WithName("AdminDeleteMessage");
    }
}

public record PatchUserRequest(
    string? Role,
    string? Status,
    int? AiAudioDailyLimit,
    bool ClearAiLimit = false);

using System.ComponentModel;
using System.Diagnostics;
using BanteraApi.Account;
using BanteraApi.Admin;
using BanteraApi.Chat;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Profile;
using BanteraApi.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BanteraApi.Mcp.Tools;

/// <summary>
/// Administrative write operations.
///
/// The class-level [Authorize] means a connection without the mcp:write scope never even
/// sees these tools in tools/list. Each method additionally calls RequireWrite() so that a
/// wiring mistake cannot silently grant write access, and every call is audited.
/// </summary>
[McpServerToolType]
[Authorize(Policy = McpAuthDefaults.WritePolicy)]
public sealed class AdminWriteTools(
    AppDbContext db,
    McpToolContext ctx,
    McpAuditLogger audit,
    AdminService admin,
    AccountDeletionService accountDeletion,
    ProfileService profiles,
    R2StorageService storage,
    ChatPushNotificationService push,
    ILogger<AdminWriteTools> logger)
{
    private static readonly string[] ValidRoles = ["user", "admin"];
    private static readonly string[] ValidStatuses = ["active", "suspended"];

    private const string TokenTailNote =
        "Already-issued access tokens remain valid for up to 60 minutes, so the change may take that long to take full effect on a signed-in device.";

    // ── Users ─────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "set_user_role")]
    [Description("""
        Change a user's role. Valid roles are 'user' and 'admin'. Promoting someone to admin
        gives them full access to the admin dashboard and this server.
        """)]
    public async Task<string> SetUserRoleAsync(
        [Description("The user to change.")] Guid userId,
        [Description("The new role: 'user' or 'admin'.")] string role,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();

        var normalized = (role ?? "").Trim().ToLowerInvariant();
        if (!ValidRoles.Contains(normalized))
            throw new McpException("role must be 'user' or 'admin'.");

        var user = await LoadTargetAsync(userId, ct);

        if (userId == ctx.AdminUserId && normalized != "admin")
            throw new McpException("You cannot remove your own admin role.");

        var before = user.Role;
        user.Role = normalized;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("set_user_role", new { userId, role = normalized }, McpAuditOutcomes.Ok,
            $"{before} -> {normalized}", userId, durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

        return McpJson.Serialize(new
        {
            ok = true,
            userId,
            name = user.Name,
            role = new { before, after = normalized },
            note = TokenTailNote,
        });
    }

    [McpServerTool(Name = "set_user_status")]
    [Description("""
        Suspend or reactivate a user. Suspending also revokes their active sessions, so the
        next time their app refreshes its token they are signed out.
        """)]
    public async Task<string> SetUserStatusAsync(
        [Description("The user to change.")] Guid userId,
        [Description("The new status: 'active' or 'suspended'.")] string status,
        [Description("Revoke the user's sessions when suspending. Default true.")] bool revokeSessions = true,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();

        var normalized = (status ?? "").Trim().ToLowerInvariant();
        if (!ValidStatuses.Contains(normalized))
            throw new McpException("status must be 'active' or 'suspended'.");

        var user = await LoadTargetAsync(userId, ct);

        if (userId == ctx.AdminUserId && normalized != "active")
            throw new McpException("You cannot suspend your own account.");

        var before = user.Status;
        user.Status = normalized;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var revoked = 0;
        if (normalized == "suspended" && revokeSessions)
        {
            revoked = await db.UserSessions
                .Where(s => s.UserId == userId && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow), ct);
        }

        await audit.WriteAsync("set_user_status", new { userId, status = normalized, revokeSessions },
            McpAuditOutcomes.Ok, $"{before} -> {normalized}, {revoked} sessions revoked", userId,
            durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

        return McpJson.Serialize(new
        {
            ok = true,
            userId,
            name = user.Name,
            status = new { before, after = normalized },
            sessionsRevoked = revoked,
            note = TokenTailNote,
        });
    }

    [McpServerTool(Name = "set_user_ai_daily_limit")]
    [Description("""
        Set how many AI audio lessons a user may generate per day. Pass null to clear the
        override and fall back to the default allowance.
        """)]
    public async Task<string> SetUserAiDailyLimitAsync(
        [Description("The user to change.")] Guid userId,
        [Description("New daily limit, 0-1000. Omit or pass null to clear the override.")] int? limit = null,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();

        if (limit is < 0 or > 1000)
            throw new McpException("limit must be between 0 and 1000, or null to clear it.");

        var user = await LoadTargetAsync(userId, ct);
        var before = user.AiAudioDailyLimit;

        user.AiAudioDailyLimit = limit;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("set_user_ai_daily_limit", new { userId, limit }, McpAuditOutcomes.Ok,
            $"{before?.ToString() ?? "default"} -> {limit?.ToString() ?? "default"}", userId,
            durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

        return McpJson.Serialize(new
        {
            ok = true,
            userId,
            name = user.Name,
            aiAudioDailyLimit = new { before, after = limit },
            note = limit is null ? "The user now uses the server default daily allowance." : null,
        });
    }

    [McpServerTool(Name = "update_user_profile")]
    [Description("""
        Edit a user's display name or language settings. Language codes are BCP-47, e.g.
        'en-US' or 'es'. Pass an empty string to clear a language.
        """)]
    public async Task<string> UpdateUserProfileAsync(
        [Description("The user to change.")] Guid userId,
        [Description("New display name, 1-80 characters.")] string? name = null,
        [Description("New native language code, or empty string to clear.")] string? nativeLanguage = null,
        [Description("New learning language code, or empty string to clear.")] string? learningLanguage = null,
        [Description("New translation language code, or empty string to clear.")] string? translationLanguage = null,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();

        var httpContext = ctx.HttpContext
            ?? throw new McpException("This action is not available outside an HTTP request.");

        await LoadTargetAsync(userId, ct);

        var (response, error) = await profiles.UpdateProfileAsync(
            userId, name, translationLanguage, nativeLanguage, learningLanguage, httpContext, ct);

        if (error is not null)
        {
            await audit.WriteAsync("update_user_profile",
                new { userId, name, nativeLanguage, learningLanguage, translationLanguage },
                McpAuditOutcomes.Error, error, userId, durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

            throw new McpException(error == "invalid_profile"
                ? "The name or one of the language codes is not valid."
                : "That user could not be updated.");
        }

        await audit.WriteAsync("update_user_profile",
            new { userId, name, nativeLanguage, learningLanguage, translationLanguage },
            McpAuditOutcomes.Ok, "updated", userId, durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

        return McpJson.Serialize(new { ok = true, userId, profile = response });
    }

    [McpServerTool(Name = "delete_user")]
    [Description("""
        Permanently delete a user account and all of their data. This cannot be undone.
        Call it first without confirm to see exactly who and what would be deleted, then call
        again with confirm set to true.
        """)]
    public async Task<string> DeleteUserAsync(
        [Description("The user to delete.")] Guid userId,
        [Description("Must be true to actually delete. Leave false to preview.")] bool confirm = false,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();

        var user = await LoadTargetAsync(userId, ct);

        var email = await db.UserIdentities
            .Where(i => i.UserId == userId && i.ProviderEmail != null)
            .Select(i => i.ProviderEmail).FirstOrDefaultAsync(ct);

        var uploads = await db.UserVideos.CountAsync(v => v.UserId == userId && !v.IsAiGenerated, ct);
        var aiAudio = await db.UserVideos.CountAsync(v => v.UserId == userId && v.IsAiGenerated, ct);

        if (!confirm)
        {
            await audit.WriteAsync("delete_user", new { userId, confirm }, McpAuditOutcomes.Preview,
                "preview only", userId, durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

            return McpJson.Serialize(new
            {
                preview = true,
                ok = false,
                user = new { id = userId, name = user.Name, email, role = user.Role, createdAt = user.CreatedAt },
                wouldDelete = new { uploads, savedItems = await db.UserSavedVideos.CountAsync(s => s.UserId == userId, ct) },
                aiAudioPreserved = aiAudio,
                warning = "This permanently deletes the account, its uploads and its chat history. AI-generated audio is kept and reassigned to the Bantera AI account. Call again with confirm: true to proceed.",
            });
        }

        if (userId == ctx.AdminUserId)
            throw new McpException("You cannot delete your own account.");

        if (user.Role == "admin")
            throw new McpException("This account is an administrator. Change its role to 'user' first if you really mean to delete it.");

        // Written before the delete so the record survives even if the process dies mid-way.
        var auditId = await audit.WriteAsync("delete_user", new { userId, confirm },
            McpAuditOutcomes.Pending, $"deleting {user.Name} <{email}>", userId, ct: ct);

        var deleted = await accountDeletion.DeleteAccountAsync(userId, ct);

        await audit.CompleteAsync(auditId, deleted ? McpAuditOutcomes.Ok : McpAuditOutcomes.Error,
            deleted ? $"deleted {user.Name} <{email}>" : "delete returned false",
            (int)sw.ElapsedMilliseconds, ct);

        if (!deleted)
            throw new McpException("That account could not be deleted.");

        return McpJson.Serialize(new
        {
            ok = true,
            deletedUserId = userId,
            name = user.Name,
            email,
            aiAudioPreserved = aiAudio,
        });
    }

    // ── Content ───────────────────────────────────────────────────────────────

    [McpServerTool(Name = "delete_video")]
    [Description("""
        Delete a video or AI audio item and its stored media. Call without confirm first to
        see what would be removed.
        """)]
    public async Task<string> DeleteVideoAsync(
        [Description("The item to delete.")] Guid videoId,
        [Description("Must be true to actually delete. Leave false to preview.")] bool confirm = false,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();

        var video = await db.UserVideos.FirstOrDefaultAsync(v => v.Id == videoId, ct)
            ?? throw new McpException("No video found with that id.");

        var savedBy = await db.UserSavedVideos.CountAsync(s => s.VideoId == videoId, ct);

        if (!confirm)
        {
            await audit.WriteAsync("delete_video", new { videoId, confirm }, McpAuditOutcomes.Preview,
                "preview only", targetId: videoId, durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

            return McpJson.Serialize(new
            {
                preview = true,
                ok = false,
                video = new
                {
                    id = video.Id,
                    fileName = video.OriginalFileName,
                    ownerUserId = video.UserId,
                    isAiGenerated = video.IsAiGenerated,
                    isPublic = video.IsPublic,
                    createdAt = video.CreatedAt,
                },
                savedByUsers = savedBy,
                warning = "This permanently deletes the item and its media file. Call again with confirm: true to proceed.",
            });
        }

        await SafeDeleteObjectAsync(video.MediaObjectKey, ct);
        if (!string.IsNullOrWhiteSpace(video.CoverImageObjectKey))
            await SafeDeleteObjectAsync(video.CoverImageObjectKey, ct);

        db.UserVideos.Remove(video);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("delete_video", new { videoId, confirm }, McpAuditOutcomes.Ok,
            $"deleted {video.OriginalFileName}", video.UserId, videoId, (int)sw.ElapsedMilliseconds, ct);

        return McpJson.Serialize(new { ok = true, deletedVideoId = videoId, fileName = video.OriginalFileName });
    }

    [McpServerTool(Name = "set_video_public")]
    [Description("Publish or unpublish a video or AI audio item.")]
    public async Task<string> SetVideoPublicAsync(
        [Description("The item to change.")] Guid videoId,
        [Description("True to publish, false to make private.")] bool isPublic,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();

        var video = await db.UserVideos.FirstOrDefaultAsync(v => v.Id == videoId, ct)
            ?? throw new McpException("No video found with that id.");

        var before = video.IsPublic;
        video.IsPublic = isPublic;
        video.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("set_video_public", new { videoId, isPublic }, McpAuditOutcomes.Ok,
            $"{before} -> {isPublic}", video.UserId, videoId, (int)sw.ElapsedMilliseconds, ct);

        return McpJson.Serialize(new
        {
            ok = true,
            videoId,
            fileName = video.OriginalFileName,
            isPublic = new { before, after = isPublic },
        });
    }

    [McpServerTool(Name = "delete_message")]
    [Description("""
        Delete a voice message and its audio. Call without confirm first to see the message
        metadata.
        """)]
    public async Task<string> DeleteMessageAsync(
        [Description("The message to delete.")] Guid messageId,
        [Description("Must be true to actually delete. Leave false to preview.")] bool confirm = false,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();

        var message = await db.ChatMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct)
            ?? throw new McpException("No message found with that id. Messages are deleted automatically after 7 days.");

        if (!confirm)
        {
            await audit.WriteAsync("delete_message", new { messageId, confirm }, McpAuditOutcomes.Preview,
                "preview only", message.SenderUserId, messageId, (int)sw.ElapsedMilliseconds, ct);

            return McpJson.Serialize(new
            {
                preview = true,
                ok = false,
                message = new
                {
                    id = message.Id,
                    threadId = message.ThreadId,
                    senderUserId = message.SenderUserId,
                    languageCode = message.SpokenLanguageCode,
                    durationSec = message.DurationMs / 1000,
                    createdAt = message.CreatedAt,
                },
                warning = "This permanently deletes the message and its audio. Call again with confirm: true to proceed.",
            });
        }

        var deleted = await admin.DeleteChatMessageAsync(messageId, ct);

        await audit.WriteAsync("delete_message", new { messageId, confirm },
            deleted ? McpAuditOutcomes.Ok : McpAuditOutcomes.Error,
            deleted ? "deleted" : "not found", message.SenderUserId, messageId, (int)sw.ElapsedMilliseconds, ct);

        if (!deleted)
            throw new McpException("That message could not be deleted.");

        return McpJson.Serialize(new { ok = true, deletedMessageId = messageId, threadId = message.ThreadId });
    }

    // ── Push ──────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "send_push_to_user")]
    [Description("""
        Send a push notification to one user's devices. Delivery is best-effort: the result
        reports how many device tokens were targeted, but actual delivery is only visible in
        the server logs.
        """)]
    public async Task<string> SendPushToUserAsync(
        [Description("The user to notify.")] Guid userId,
        [Description("Notification title, 1-60 characters.")] string title,
        [Description("Notification body, 1-200 characters.")] string body,
        [Description("Skip users who turned notifications off. Default true.")] bool respectPreference = true,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();
        ValidatePushText(title, body);

        var user = await LoadTargetAsync(userId, ct);

        if (respectPreference && !user.ChatNotificationsEnabled)
        {
            await audit.WriteAsync("send_push_to_user", new { userId, title }, McpAuditOutcomes.Denied,
                "user has notifications disabled", userId, durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

            return McpJson.Serialize(new
            {
                ok = false,
                userId,
                reason = "This user has turned notifications off. Pass respectPreference: false to send anyway.",
            });
        }

        var tokens = await db.UserPushTokens.Where(t => t.UserId == userId).ToListAsync(ct);
        if (tokens.Count > 0)
            await push.SendAsync(tokens, title, body, new Dictionary<string, string>(), ct);

        await audit.WriteAsync("send_push_to_user", new { userId, title, body }, McpAuditOutcomes.Ok,
            $"{tokens.Count} tokens", userId, durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

        return McpJson.Serialize(new
        {
            ok = true,
            userId,
            name = user.Name,
            tokensTargeted = tokens.Count,
            note = tokens.Count == 0 ? "This user has no registered devices, so nothing was sent." : null,
        });
    }

    [McpServerTool(Name = "send_push_to_segment")]
    [Description("""
        Send a push notification to a filtered group of users. Always preview first with
        push_segment_preview or by calling this without confirm. Capped at 500 users per call.
        """)]
    public async Task<string> SendPushToSegmentAsync(
        [Description("Notification title, 1-60 characters.")] string title,
        [Description("Notification body, 1-200 characters.")] string body,
        [Description("Learning language, e.g. 'es'. Matched by language family.")] string? learningLanguage = null,
        [Description("Native language, e.g. 'zh-TW'. Matched by language family.")] string? nativeLanguage = null,
        [Description("Only users active within this many days.")] int? activeWithinDays = null,
        [Description("Account status to include. Default 'active'.")] string? status = null,
        [Description("Maximum users to notify. 1-500, default 100.")] int limit = 100,
        [Description("Must be true to actually send. Leave false to preview.")] bool confirm = false,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();
        ValidatePushText(title, body);

        limit = Math.Clamp(limit, 1, 500);

        var segment = await PushTools.BuildSegmentAsync(
            db, learningLanguage, nativeLanguage, activeWithinDays, status, requirePushToken: true, ct);

        var selected = segment.Users.Take(limit).ToList();
        var args = new { title, body, learningLanguage, nativeLanguage, activeWithinDays, status, limit };

        if (!confirm)
        {
            await audit.WriteAsync("send_push_to_segment", args, McpAuditOutcomes.Preview,
                $"{selected.Count} users would be notified", durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

            return McpJson.Serialize(new
            {
                preview = true,
                ok = false,
                matchedUsers = segment.Users.Count,
                wouldNotify = selected.Count,
                truncated = segment.Users.Count > limit,
                sample = selected.Take(10).Select(u => new { userId = u.Id, name = u.Name }),
                warning = "Call again with confirm: true to send. This cannot be undone.",
            });
        }

        var ids = selected.Select(u => u.Id).ToList();
        var tokens = await db.UserPushTokens.Where(t => ids.Contains(t.UserId)).ToListAsync(ct);

        // Sent in batches so one bad token does not stall the whole run.
        var sent = 0;
        foreach (var batch in tokens.Chunk(50))
        {
            try
            {
                await push.SendAsync(batch, title, body, new Dictionary<string, string>(), ct);
                sent += batch.Length;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[MCP] Segment push batch failed");
            }
        }

        await audit.WriteAsync("send_push_to_segment", args, McpAuditOutcomes.Ok,
            $"{selected.Count} users, {sent} tokens", durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

        return McpJson.Serialize(new
        {
            ok = true,
            usersTargeted = selected.Count,
            tokensTargeted = sent,
            truncated = segment.Users.Count > limit,
        });
    }

    // ── Maintenance ───────────────────────────────────────────────────────────

    [McpServerTool(Name = "analytics_backfill_activity")]
    [Description("""
        Rebuild the approximate activity history from existing content, chat, session and
        login timestamps. Safe to re-run — existing rows are never overwritten. Normally
        unnecessary, as it runs once automatically.
        """)]
    public async Task<string> BackfillActivityAsync(
        [Description("Must be true to run. Leave false to see what it would do.")] bool confirm = false,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();

        if (!confirm)
        {
            return McpJson.Serialize(new
            {
                preview = true,
                ok = false,
                what = "Reconstructs user_activity_daily rows for days before activity tracking went live, from video, AI job, chat, save, session, push-token and login timestamps.",
                warning = "Existing rows are preserved; only missing days are added. Call again with confirm: true to run.",
            });
        }

        var before = await db.UserActivityDaily.CountAsync(ct);
        await db.Database.ExecuteSqlRawAsync(Activity.ActivityBackfill.Sql, ct);
        var after = await db.UserActivityDaily.CountAsync(ct);

        await audit.WriteAsync("analytics_backfill_activity", new { confirm }, McpAuditOutcomes.Ok,
            $"{after - before} rows added", durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

        return McpJson.Serialize(new { ok = true, rowsBefore = before, rowsAfter = after, rowsAdded = after - before });
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<User> LoadTargetAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new McpException("No user found with that id.");

        if (user.Role == "system")
            throw new McpException("That is the Bantera AI system account and cannot be modified.");

        return user;
    }

    private static void ValidatePushText(string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 60)
            throw new McpException("title must be between 1 and 60 characters.");

        if (string.IsNullOrWhiteSpace(body) || body.Length > 200)
            throw new McpException("body must be between 1 and 200 characters.");
    }

    private async Task SafeDeleteObjectAsync(string key, CancellationToken ct)
    {
        try
        {
            await storage.DeleteObjectAsync(key, ct);
        }
        catch (Exception ex)
        {
            // The database row still goes away; a stranded object is preferable to a failed delete.
            logger.LogWarning(ex, "[MCP] Failed to delete storage object {Key}", key);
        }
    }
}

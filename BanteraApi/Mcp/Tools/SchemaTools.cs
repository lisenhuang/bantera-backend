using System.ComponentModel;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace BanteraApi.Mcp.Tools;

/// <summary>
/// Self-description tools. These let the model discover what data exists and what the
/// numbers mean before reaching for a metric, which avoids confidently wrong answers.
/// </summary>
[McpServerToolType]
public sealed class SchemaTools(AppDbContext db, McpToolContext ctx)
{
    [McpServerTool(Name = "describe_schema")]
    [Description("""
        Describes the Bantera data model available to these tools: what entities exist, the
        enumerations they use, how languages are grouped, when activity tracking started, and
        the known caveats that affect how numbers should be interpreted. Call this first when
        unsure what a metric means or which tool to use.
        """)]
    public async Task<string> DescribeSchemaAsync(CancellationToken ct = default)
    {
        var liveSince = await db.UserActivityDaily
            .Where(a => a.Source == "live")
            .MinAsync(a => (DateOnly?)a.Date, ct);

        var backfilledFrom = await db.UserActivityDaily.MinAsync(a => (DateOnly?)a.Date, ct);

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            entities = new object[]
            {
                new { name = "users", what = "One row per account.", fields = new[] { "id", "name", "role (user|admin|system)", "status (active|suspended|system)", "nativeLanguage", "learningLanguage", "translationLanguage", "aiAudioDailyLimit", "createdAt", "lastLoginAt" } },
                new { name = "identities", what = "Sign-in methods per user.", fields = new[] { "provider (apple|google|email)", "providerEmail", "createdAt" } },
                new { name = "videos", what = "Uploaded videos and AI-generated audio lessons.", fields = new[] { "isAiGenerated", "isPublic", "mediaContentType", "transcriptLanguageCode", "durationMs", "fileSizeBytes", "createdAt" } },
                new { name = "aiAudioJobs", what = "AI audio generation attempts.", fields = new[] { "status (processing|done|failed)", "languageCode", "scenarioId", "createdAt", "completedAt" } },
                new { name = "chat", what = "Voice message threads.", fields = new[] { "threadType (dm|group)", "spokenLanguageCode", "durationMs", "createdAt" } },
                new { name = "pushTokens", what = "APNs device tokens.", fields = new[] { "platform", "isSandbox", "supportsCalls", "lastSeenAt" } },
                new { name = "activity", what = "One row per user per UTC day they used the app. Backs DAU/WAU/MAU.", fields = new[] { "date", "touchCount", "messagesSent", "source (live|backfill)" } },
            },
            languageGrouping = "Language codes are BCP-47 and stored as free text, so casing varies. Tools group them into language families by default: en-US and en-GB both count as 'en', while zh-HK and zh-TW stay distinct. Pass groupBy='exact' to see raw codes.",
            activityTracking = new
            {
                liveSince,
                backfilledFrom,
                note = "Days before liveSince are reconstructed from content, chat, session and login timestamps. They undercount users who only browsed without creating anything.",
            },
            caveats = new[]
            {
                "Deleted accounts are removed entirely, so registration and cohort counts only cover accounts that still exist.",
                "Chat messages are deleted 7 days after they are sent; message history beyond that comes from the per-day counters in the activity table.",
                "The 'Bantera AI' system account owns audio preserved from deleted users and is excluded from user metrics.",
                "All timestamps are UTC.",
            },
            periodSyntax = "'7d', '12w', '6m', 'today', 'yesterday', '2026-01-01', or '2026-01-01..2026-02-01' (end inclusive).",
        });
    }

    [McpServerTool(Name = "list_metrics")]
    [Description("""
        Lists every tool available on this server with a one-line description of what it
        answers, plus which permissions the current connection holds. Use it to pick the
        right tool for a question.
        """)]
    public string ListMetrics()
    {
        return McpJson.Serialize(new
        {
            callerScopes = ctx.Scopes.OrderBy(s => s),
            canWrite = ctx.CanWrite,
            periodSyntax = "'7d', '12w', '6m', 'today', 'yesterday', '2026-01-01', or '2026-01-01..2026-02-01'.",
            readTools = new object[]
            {
                new { name = "users_overview", what = "Headline user counts: totals, roles, statuses, sign-in providers, current DAU/WAU/MAU." },
                new { name = "registrations_timeseries", what = "Sign-ups over time, optionally split by sign-in provider." },
                new { name = "active_users", what = "DAU with rolling WAU/MAU for each day in a period." },
                new { name = "retention_cohorts", what = "Weekly signup cohorts and how many came back in later weeks." },
                new { name = "language_breakdown", what = "Users per learning, native or translation language." },
                new { name = "language_matrix", what = "Most common native-language to learning-language pairs." },
                new { name = "top_users", what = "Most active users by videos, AI audio, messages, saves or active days." },
                new { name = "content_overview", what = "Library totals: uploads vs AI audio, storage, durations, AI job success." },
                new { name = "content_timeseries", what = "Content created per bucket with storage added." },
                new { name = "content_by_language", what = "Content counts per transcript language." },
                new { name = "ai_jobs_stats", what = "AI audio job success and failure rates by language, scenario or day." },
                new { name = "diagnostics_summary", what = "AI short-cue diagnostics grouped by reason and language." },
                new { name = "video_lookup", what = "Find videos by id, owner or filename." },
                new { name = "chat_overview", what = "Chat health: threads, recent senders, groups, blocks." },
                new { name = "chat_timeseries", what = "Messages sent per day (durable counters)." },
                new { name = "chat_by_language", what = "Recent messages grouped by spoken language." },
                new { name = "message_lookup", what = "Find chat messages by id, thread or sender (last 7 days)." },
                new { name = "push_token_stats", what = "Push reach: tokens, platforms, freshness, opt-outs." },
                new { name = "push_segment_preview", what = "How many users a segment push would reach (counts only)." },
                new { name = "user_lookup", what = "Find users by id, email or name." },
                new { name = "user_detail", what = "Full admin view of one user." },
                new { name = "users_list", what = "Filterable, paged user list for segmentation." },
                new { name = "audit_log_list", what = "Recent admin actions taken through this server." },
                new { name = "describe_schema", what = "The data model, enumerations and caveats." },
            },
            writeTools = new object[]
            {
                new { name = "set_user_role", what = "Promote or demote a user.", requiresConfirm = false },
                new { name = "set_user_status", what = "Suspend or reactivate a user.", requiresConfirm = false },
                new { name = "set_user_ai_daily_limit", what = "Change a user's daily AI audio allowance.", requiresConfirm = false },
                new { name = "update_user_profile", what = "Edit a user's name or languages.", requiresConfirm = false },
                new { name = "delete_user", what = "Permanently delete an account.", requiresConfirm = true },
                new { name = "delete_video", what = "Delete a video or AI audio item.", requiresConfirm = true },
                new { name = "set_video_public", what = "Publish or unpublish an item.", requiresConfirm = false },
                new { name = "delete_message", what = "Delete a voice message.", requiresConfirm = true },
                new { name = "send_push_to_user", what = "Send a push notification to one user.", requiresConfirm = false },
                new { name = "send_push_to_segment", what = "Send a push to a filtered group of users.", requiresConfirm = true },
                new { name = "analytics_backfill_activity", what = "Rebuild approximate activity history.", requiresConfirm = true },
            },
            note = ctx.CanWrite
                ? "This connection can read and write."
                : "This connection is read-only; write tools are not available.",
        });
    }
}

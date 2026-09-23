namespace BanteraApi.Activity;

/// <summary>
/// One-time reconstruction of activity history from timestamps that already exist in the
/// database, so DAU/WAU/MAU have some history before live tracking started.
///
/// Shared by the migration that seeds it and the analytics_backfill_activity tool, so there
/// is exactly one definition. Safe to re-run: existing rows are never overwritten.
///
/// The source list is assembled at run time from the tables that actually exist, so the
/// backfill does not depend on migration ordering or on the bootstrap SQL in Program.cs
/// having run first.
/// </summary>
public static class ActivityBackfill
{
    public const string Sql = """
        DO $backfill$
        DECLARE
            parts text[] := ARRAY[]::text[];
            stmt  text;
        BEGIN
            IF to_regclass('public.user_videos') IS NOT NULL THEN
                parts := array_append(parts, 'SELECT "UserId" AS uid, "CreatedAt" AS ts FROM user_videos');
            END IF;
            IF to_regclass('public.user_audio_jobs') IS NOT NULL THEN
                parts := array_append(parts, 'SELECT "UserId", "CreatedAt" FROM user_audio_jobs');
            END IF;
            IF to_regclass('public.chat_messages') IS NOT NULL THEN
                parts := array_append(parts, 'SELECT "SenderUserId", "CreatedAt" FROM chat_messages');
            END IF;
            IF to_regclass('public.chat_message_receipts') IS NOT NULL THEN
                parts := array_append(parts, 'SELECT "UserId", "ReceivedAt" FROM chat_message_receipts');
            END IF;
            IF to_regclass('public.chat_thread_memberships') IS NOT NULL THEN
                parts := array_append(parts, 'SELECT "UserId", "LastReadAt" FROM chat_thread_memberships WHERE "LastReadAt" IS NOT NULL');
            END IF;
            IF to_regclass('public.user_saved_videos') IS NOT NULL THEN
                parts := array_append(parts, 'SELECT "UserId", "SavedAt" FROM user_saved_videos');
            END IF;
            IF to_regclass('public.user_saved_cues') IS NOT NULL THEN
                parts := array_append(parts, 'SELECT "UserId", "SavedAt" FROM user_saved_cues');
            END IF;
            IF to_regclass('public.user_sessions') IS NOT NULL THEN
                parts := array_append(parts, 'SELECT "UserId", "CreatedAt" FROM user_sessions');
            END IF;
            IF to_regclass('public.user_push_tokens') IS NOT NULL THEN
                parts := array_append(parts, 'SELECT "UserId", "CreatedAt" FROM user_push_tokens');
                parts := array_append(parts, 'SELECT "UserId", "LastSeenAt" FROM user_push_tokens');
            END IF;
            parts := array_append(parts, 'SELECT "Id", "CreatedAt" FROM users');
            parts := array_append(parts, 'SELECT "Id", "LastLoginAt" FROM users WHERE "LastLoginAt" IS NOT NULL');

            stmt := 'INSERT INTO user_activity_daily ("UserId","Date","FirstSeenAt","LastSeenAt","TouchCount","MessagesSent","Source") '
                 || 'SELECT s.uid, (s.ts AT TIME ZONE ''UTC'')::date, MIN(s.ts), MAX(s.ts), COUNT(*), 0, ''backfill'' '
                 || 'FROM (' || array_to_string(parts, ' UNION ALL ') || ') s '
                 || 'JOIN users u ON u."Id" = s.uid AND u."Role" <> ''system'' '
                 || 'WHERE s.ts IS NOT NULL AND s.ts < now() '
                 || 'GROUP BY 1, 2 '
                 || 'ON CONFLICT ("UserId","Date") DO NOTHING';

            EXECUTE stmt;
        END
        $backfill$;
        """;

    /// <summary>
    /// Indexes that the analytics queries rely on. Created alongside the activity table
    /// because none of these columns were indexed before.
    /// </summary>
    public const string AnalyticsIndexesSql = """
        CREATE INDEX IF NOT EXISTS "IX_users_CreatedAt" ON users ("CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_users_LastLoginAt" ON users ("LastLoginAt");
        CREATE INDEX IF NOT EXISTS "IX_users_NativeLanguage" ON users ("NativeLanguage");
        CREATE INDEX IF NOT EXISTS "IX_users_LearningLanguage" ON users ("LearningLanguage");
        CREATE INDEX IF NOT EXISTS "IX_users_Role_Status" ON users ("Role", "Status");
        CREATE INDEX IF NOT EXISTS "IX_user_videos_CreatedAt" ON user_videos ("CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_user_videos_TranscriptLanguageCode" ON user_videos ("TranscriptLanguageCode");
        """;

    public const string DropAnalyticsIndexesSql = """
        DROP INDEX IF EXISTS "IX_users_CreatedAt";
        DROP INDEX IF EXISTS "IX_users_LastLoginAt";
        DROP INDEX IF EXISTS "IX_users_NativeLanguage";
        DROP INDEX IF EXISTS "IX_users_LearningLanguage";
        DROP INDEX IF EXISTS "IX_users_Role_Status";
        DROP INDEX IF EXISTS "IX_user_videos_CreatedAt";
        DROP INDEX IF EXISTS "IX_user_videos_TranscriptLanguageCode";
        """;
}

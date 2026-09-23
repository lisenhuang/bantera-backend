using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BanteraApi.Database;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserIdentity> UserIdentities => Set<UserIdentity>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UserVideo> UserVideos => Set<UserVideo>();
    public DbSet<UserAudioJob> UserAudioJobs => Set<UserAudioJob>();
    public DbSet<AiAudioShortCueDiagnostic> AiAudioShortCueDiagnostics => Set<AiAudioShortCueDiagnostic>();
    public DbSet<UserSavedVideo> UserSavedVideos => Set<UserSavedVideo>();
    public DbSet<UserSavedCue> UserSavedCues => Set<UserSavedCue>();
    public DbSet<ChatThread> ChatThreads => Set<ChatThread>();
    public DbSet<ChatThreadMembership> ChatThreadMemberships => Set<ChatThreadMembership>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatMessageReceipt> ChatMessageReceipts => Set<ChatMessageReceipt>();
    public DbSet<ChatBlock> ChatBlocks => Set<ChatBlock>();
    public DbSet<UserPushToken> UserPushTokens => Set<UserPushToken>();
    public DbSet<UserActivityDaily> UserActivityDaily => Set<UserActivityDaily>();
    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();
    public DbSet<OAuthAuthorizationCode> OAuthAuthorizationCodes => Set<OAuthAuthorizationCode>();
    public DbSet<OAuthRefreshToken> OAuthRefreshTokens => Set<OAuthRefreshToken>();
    public DbSet<McpAuditLog> McpAuditLogs => Set<McpAuditLog>();

    // Stores a string list as a JSON array. Explicit rather than relying on provider
    // defaults, which map List<string> to text[] unless told otherwise.
    private static readonly ValueConverter<List<string>, string> StringListConverter = new(
        v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
        v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>());

    private static readonly ValueComparer<List<string>> StringListComparer = new(
        (a, b) => a != null && b != null && a.SequenceEqual(b),
        v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode())),
        v => v.ToList());

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.TranslationLanguage).HasMaxLength(35);
            e.Property(x => x.NativeLanguage).HasMaxLength(35);
            e.Property(x => x.LearningLanguage).HasMaxLength(35);
            e.Property(x => x.ChatNotificationsEnabled).HasDefaultValue(true).IsRequired();
            e.Property(x => x.AvatarObjectKey).HasMaxLength(255);
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.Property(x => x.AlwaysOnline).HasDefaultValue(false).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();
        });

        b.Entity<UserIdentity>(e =>
        {
            e.ToTable("user_identities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Provider).HasMaxLength(50).IsRequired();
            e.Property(x => x.ProviderUserId).HasMaxLength(255).IsRequired();
            e.Property(x => x.ProviderEmail).HasMaxLength(255);
            e.Property(x => x.PasswordHash).HasMaxLength(255);
            e.HasIndex(x => new { x.Provider, x.ProviderUserId }).IsUnique();
            e.HasOne(x => x.User)
             .WithMany(x => x.Identities)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("user_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.RefreshTokenHash).HasMaxLength(255).IsRequired();
            e.Property(x => x.RefreshTokenLookup).HasMaxLength(64);
            e.HasIndex(x => x.RefreshTokenLookup)
                .IsUnique()
                .HasFilter("\"RefreshTokenLookup\" IS NOT NULL");
            e.Property(x => x.DeviceName).HasMaxLength(255);
            e.HasOne(x => x.User)
             .WithMany(x => x.Sessions)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserVideo>(e =>
        {
            e.ToTable("user_videos");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.MediaObjectKey).HasMaxLength(255).IsRequired();
            e.Property(x => x.MediaContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.TranscriptLanguage).HasMaxLength(35).IsRequired();
            e.Property(x => x.TranscriptLanguageCode).HasMaxLength(16).IsRequired();
            e.Property(x => x.TranscriptText).IsRequired();
            e.Property(x => x.TranscriptCuesJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.TranscriptShortCuesJson).HasColumnType("jsonb");
            e.Property(x => x.DialogueLinesJson).HasColumnType("jsonb");
            e.Property(x => x.WordTimingJson).HasColumnType("jsonb");
            e.Property(x => x.CoverImageObjectKey).HasMaxLength(255);
            e.Property(x => x.RemovedFromOwnerListAt);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne(x => x.User)
             .WithMany(x => x.Videos)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserAudioJob>(e =>
        {
            e.ToTable("user_audio_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.LanguageCode).HasMaxLength(16);
            e.Property(x => x.ScenarioId).HasMaxLength(80);
            e.Property(x => x.CreatedAt).IsRequired();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.Status, x.CreatedAt });
            e.HasOne<User>()
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserSavedVideo>(e =>
        {
            e.ToTable("user_saved_videos");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.SavedAt).IsRequired();
            e.HasIndex(x => new { x.UserId, x.VideoId }).IsUnique();
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Video)
             .WithMany()
             .HasForeignKey(x => x.VideoId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserSavedCue>(e =>
        {
            e.ToTable("user_saved_cues");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.CueId).HasMaxLength(255).IsRequired();
            e.Property(x => x.SavedAt).IsRequired();
            e.HasIndex(x => new { x.UserId, x.VideoId, x.CueId }).IsUnique();
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Video)
             .WithMany()
             .HasForeignKey(x => x.VideoId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AiAudioShortCueDiagnostic>(e =>
        {
            e.ToTable("ai_audio_short_cue_diagnostics");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.LanguageCode).HasMaxLength(16).IsRequired();
            e.Property(x => x.ScenarioId).HasMaxLength(80);
            e.Property(x => x.Reason).HasMaxLength(100).IsRequired();
            e.Property(x => x.LongAlignmentMode).HasMaxLength(50);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.DetailJson).HasColumnType("jsonb");
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.Reason);
            e.HasIndex(x => new { x.LanguageCode, x.Reason });
        });

        b.Entity<ChatThread>(e =>
        {
            e.ToTable("chat_threads");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Type).HasMaxLength(20).IsRequired();
            e.Property(x => x.DirectMessageKey).HasMaxLength(80);
            e.Property(x => x.LanguageKey).HasMaxLength(35);
            e.Property(x => x.LanguageDisplayName).HasMaxLength(80);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();
            e.HasIndex(x => new { x.Type, x.DirectMessageKey })
                .IsUnique()
                .HasFilter("\"DirectMessageKey\" IS NOT NULL");
            e.HasIndex(x => new { x.Type, x.LanguageKey })
                .IsUnique()
                .HasFilter("\"LanguageKey\" IS NOT NULL");
        });

        b.Entity<ChatThreadMembership>(e =>
        {
            e.ToTable("chat_thread_memberships");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.UnreadCount).HasDefaultValue(0).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();
            e.HasIndex(x => new { x.ThreadId, x.UserId }).IsUnique();
            e.HasOne(x => x.Thread)
             .WithMany(x => x.Memberships)
             .HasForeignKey(x => x.ThreadId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User)
             .WithMany(x => x.ChatMemberships)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.AudioObjectKey).HasMaxLength(255).IsRequired();
            e.Property(x => x.AudioContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.SpokenLanguageCode).HasMaxLength(35).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.HasIndex(x => new { x.ThreadId, x.CreatedAt });
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne(x => x.Thread)
             .WithMany(x => x.Messages)
             .HasForeignKey(x => x.ThreadId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.SenderUser)
             .WithMany(x => x.SentChatMessages)
             .HasForeignKey(x => x.SenderUserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatMessageReceipt>(e =>
        {
            e.ToTable("chat_message_receipts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.ReceivedAt).IsRequired();
            e.HasIndex(x => new { x.MessageId, x.UserId }).IsUnique();
            e.HasOne(x => x.Message)
             .WithMany(x => x.Receipts)
             .HasForeignKey(x => x.MessageId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatBlock>(e =>
        {
            e.ToTable("chat_blocks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.CreatedAt).IsRequired();
            e.HasIndex(x => new { x.BlockerUserId, x.BlockedUserId }).IsUnique();
            e.HasOne(x => x.BlockerUser)
             .WithMany(x => x.BlockedUsers)
             .HasForeignKey(x => x.BlockerUserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.BlockedUser)
             .WithMany(x => x.BlockedByUsers)
             .HasForeignKey(x => x.BlockedUserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserPushToken>(e =>
        {
            e.ToTable("user_push_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Platform).HasMaxLength(20).IsRequired();
            e.Property(x => x.Token).HasMaxLength(255).IsRequired();
            e.Property(x => x.SupportsCalls).IsRequired().HasDefaultValue(false);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();
            e.Property(x => x.LastSeenAt).IsRequired();
            e.HasIndex(x => new { x.UserId, x.Token }).IsUnique();
            e.HasOne(x => x.User)
             .WithMany(x => x.PushTokens)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Activity tracking ─────────────────────────────────────────────────
        b.Entity<UserActivityDaily>(e =>
        {
            e.ToTable("user_activity_daily");
            // Composite key; no FK to users on purpose — history must survive account deletion.
            e.HasKey(x => new { x.UserId, x.Date });
            e.Property(x => x.Date).HasColumnType("date").IsRequired();
            e.Property(x => x.FirstSeenAt).IsRequired();
            e.Property(x => x.LastSeenAt).IsRequired();
            e.Property(x => x.TouchCount).IsRequired().HasDefaultValue(1);
            e.Property(x => x.MessagesSent).IsRequired().HasDefaultValue(0);
            e.Property(x => x.Source).HasMaxLength(10).IsRequired().HasDefaultValue(ActivitySources.Live);
            e.HasIndex(x => x.Date);
        });

        // ── MCP OAuth ─────────────────────────────────────────────────────────
        b.Entity<OAuthClient>(e =>
        {
            e.ToTable("oauth_clients");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.ClientId).HasMaxLength(128).IsRequired();
            e.Property(x => x.ClientSecretHash).HasMaxLength(255);
            e.Property(x => x.ClientName).HasMaxLength(200).IsRequired();
            e.Property(x => x.RedirectUris)
             .HasColumnType("jsonb").IsRequired()
             .HasConversion(StringListConverter, StringListComparer);
            e.Property(x => x.GrantTypes)
             .HasColumnType("jsonb").IsRequired()
             .HasConversion(StringListConverter, StringListComparer);
            e.Property(x => x.TokenEndpointAuthMethod).HasMaxLength(40).IsRequired();
            e.Property(x => x.ClientUri).HasMaxLength(500);
            e.Property(x => x.IsStatic).IsRequired().HasDefaultValue(false);
            e.Property(x => x.CreatedAt).IsRequired();
            e.HasIndex(x => x.ClientId).IsUnique();
        });

        b.Entity<OAuthAuthorizationCode>(e =>
        {
            e.ToTable("oauth_authorization_codes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.RedirectUri).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Resource).HasMaxLength(500).IsRequired();
            e.Property(x => x.RequestedScopes).HasMaxLength(200).IsRequired();
            e.Property(x => x.GrantedScopes).HasMaxLength(200);
            e.Property(x => x.CodeChallenge).HasMaxLength(128).IsRequired();
            e.Property(x => x.CodeChallengeMethod).HasMaxLength(10).IsRequired();
            e.Property(x => x.State).HasMaxLength(1024);
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.ExpiresAt).IsRequired();
            // Filtered unique index, same pattern as UserSession.RefreshTokenLookup.
            e.HasIndex(x => x.CodeHash).IsUnique().HasFilter("\"CodeHash\" IS NOT NULL");
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne(x => x.Client)
             .WithMany()
             .HasForeignKey(x => x.OAuthClientId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OAuthRefreshToken>(e =>
        {
            e.ToTable("oauth_refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.TokenLookup).HasMaxLength(64).IsRequired();
            e.Property(x => x.FamilyId).IsRequired();
            e.Property(x => x.Scopes).HasMaxLength(200).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.ExpiresAt).IsRequired();
            e.HasIndex(x => x.TokenLookup).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Client)
             .WithMany(x => x.RefreshTokens)
             .HasForeignKey(x => x.OAuthClientId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<McpAuditLog>(e =>
        {
            e.ToTable("mcp_audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.AdminUserId).IsRequired();
            e.Property(x => x.ClientId).HasMaxLength(200);
            e.Property(x => x.Tool).HasMaxLength(100).IsRequired();
            e.Property(x => x.ArgsJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Outcome).HasMaxLength(20).IsRequired();
            e.Property(x => x.ResultSummary).HasMaxLength(1000);
            e.Property(x => x.DurationMs).IsRequired();
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.AdminUserId, x.CreatedAt });
            e.HasIndex(x => x.TargetUserId);
            e.HasIndex(x => new { x.Tool, x.CreatedAt });
        });
    }
}

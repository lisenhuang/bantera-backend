using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;

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
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();
            e.Property(x => x.LastSeenAt).IsRequired();
            e.HasIndex(x => new { x.UserId, x.Token }).IsUnique();
            e.HasOne(x => x.User)
             .WithMany(x => x.PushTokens)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

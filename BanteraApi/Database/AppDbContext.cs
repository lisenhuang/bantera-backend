using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Database;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserIdentity> UserIdentities => Set<UserIdentity>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UserVideo> UserVideos => Set<UserVideo>();
    public DbSet<UserSavedVideo> UserSavedVideos => Set<UserSavedVideo>();
    public DbSet<UserSavedCue> UserSavedCues => Set<UserSavedCue>();

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
    }
}

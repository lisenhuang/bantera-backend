using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Database;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserIdentity> UserIdentities => Set<UserIdentity>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
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
            e.Property(x => x.DeviceName).HasMaxLength(255);
            e.HasOne(x => x.User)
             .WithMany(x => x.Sessions)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

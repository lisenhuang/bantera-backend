using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Database;

public static class DataSeeder
{
    public static async Task SeedAsync(AppDbContext db, ILogger logger)
    {
        const string testEmail = "test@bantera.app";

        var exists = await db.UserIdentities.AnyAsync(i =>
            i.Provider == "email" && i.ProviderUserId == testEmail);

        if (exists)
        {
            logger.LogInformation("[Seed] Test user already exists — skipping.");
            return;
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = "Test User",
            Status = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);

        db.UserIdentities.Add(new UserIdentity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Provider = "email",
            ProviderUserId = testEmail,
            ProviderEmail = testEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Test1234!"),
            EmailVerifiedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        logger.LogInformation("[Seed] Test user created — email: {Email}", testEmail);
    }
}

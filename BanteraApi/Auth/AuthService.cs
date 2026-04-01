using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BanteraApi.Auth;

public class AuthService(AppDbContext db, JwtService jwt, IOptions<JwtSettings> options)
{
    private readonly JwtSettings _settings = options.Value;

    public async Task<LoginResponse?> LoginAsync(string email, string password)
    {
        var identity = await db.UserIdentities
            .Include(i => i.User)
            .FirstOrDefaultAsync(i =>
                i.Provider == "email" &&
                i.ProviderUserId == email.ToLowerInvariant());

        if (identity is null || identity.PasswordHash is null)
            return null;

        if (!BCrypt.Net.BCrypt.Verify(password, identity.PasswordHash))
            return null;

        if (identity.User.Status != "active")
            return null;

        // Update last login
        identity.User.LastLoginAt = DateTime.UtcNow;
        identity.User.UpdatedAt = DateTime.UtcNow;

        // Issue tokens
        var accessToken = jwt.GenerateAccessToken(identity.UserId);
        var plainRefreshToken = jwt.GenerateRefreshToken();

        db.UserSessions.Add(new UserSession
        {
            UserId = identity.UserId,
            RefreshTokenHash = jwt.HashRefreshToken(plainRefreshToken),
            ExpiresAt = DateTime.UtcNow.AddDays(_settings.RefreshTokenExpiryDays),
            CreatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();

        return new LoginResponse(
            AccessToken: accessToken,
            TokenType: "Bearer",
            ExpiresIn: _settings.AccessTokenExpiryMinutes * 60,
            RefreshToken: plainRefreshToken
        );
    }
}

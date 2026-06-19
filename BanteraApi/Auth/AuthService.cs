using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BanteraApi.Auth;

public class AuthService(
    AppDbContext db,
    JwtService jwt,
    AppleIdentityTokenValidator appleTokenValidator,
    GoogleIdentityTokenValidator googleTokenValidator,
    ProfileService profileService,
    IOptions<JwtSettings> options)
{
    private readonly JwtSettings _settings = options.Value;

    public async Task<LoginResponse?> LoginAsync(string email, string password)
    {
        var normalizedEmail = NormalizeEmail(email);
        var identity = await db.UserIdentities
            .Include(i => i.User)
            .FirstOrDefaultAsync(i =>
                i.Provider == "email" &&
                i.ProviderUserId == normalizedEmail);

        if (identity is null || identity.PasswordHash is null)
            return null;

        if (!BCrypt.Net.BCrypt.Verify(password, identity.PasswordHash))
            return null;

        if (identity.User.Status != "active")
            return null;

        return await IssueSessionAsync(identity.User);
    }

    public async Task<(LoginResponse? Response, string? ErrorCode)> RegisterAsync(string email, string password)
    {
        var normalizedEmail = NormalizeEmail(email);
        var emailIdentityExists = await db.UserIdentities.AnyAsync(i =>
            i.Provider == "email" &&
            i.ProviderUserId == normalizedEmail);

        if (emailIdentityExists)
            return (null, ErrorCodes.EmailAlreadyRegistered);

        var now = DateTime.UtcNow;
        var user = new User
        {
            Name = DefaultNameFromEmail(normalizedEmail),
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Users.Add(user);
        db.UserIdentities.Add(new UserIdentity
        {
            User = user,
            Provider = "email",
            ProviderUserId = normalizedEmail,
            ProviderEmail = normalizedEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            CreatedAt = now,
            UpdatedAt = now,
        });

        return (await IssueSessionAsync(user), null);
    }

    public async Task<(LoginResponse? Response, string ErrorCode)> LoginWithAppleAsync(
        AppleLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = await appleTokenValidator.ValidateAsync(
            request.IdentityToken,
            request.UserIdentifier,
            cancellationToken);

        if (!validation.IsValid || string.IsNullOrWhiteSpace(validation.Subject))
            return (null, validation.ErrorCode ?? ErrorCodes.InvalidAppleToken);

        var identity = await db.UserIdentities
            .Include(i => i.User)
            .FirstOrDefaultAsync(i =>
                i.Provider == "apple" &&
                i.ProviderUserId == validation.Subject,
                cancellationToken);

        var now = DateTime.UtcNow;
        if (identity is null)
        {
            var user = new User
            {
                Name = ResolveAppleName(
                    request.GivenName,
                    request.FamilyName,
                    validation.Email ?? request.Email),
                Status = "active",
                CreatedAt = now,
                UpdatedAt = now,
            };

            identity = new UserIdentity
            {
                User = user,
                Provider = "apple",
                ProviderUserId = validation.Subject,
                ProviderEmail = NormalizeOptionalEmail(validation.Email ?? request.Email),
                EmailVerifiedAt = validation.EmailVerified ? now : null,
                CreatedAt = now,
                UpdatedAt = now,
            };

            db.Users.Add(user);
            db.UserIdentities.Add(identity);
        }
        else
        {
            if (identity.User.Status != "active")
                return (null, ErrorCodes.Unauthorized);

            if (string.IsNullOrWhiteSpace(identity.ProviderEmail))
            {
                identity.ProviderEmail = NormalizeOptionalEmail(validation.Email ?? request.Email);
            }

            if (identity.EmailVerifiedAt is null && validation.EmailVerified)
            {
                identity.EmailVerifiedAt = now;
            }

            identity.UpdatedAt = now;
        }

        return (await IssueSessionAsync(identity.User), string.Empty);
    }

    public async Task<(LoginResponse? Response, string ErrorCode)> LoginWithGoogleAsync(
        string idToken,
        CancellationToken cancellationToken = default)
    {
        var validation = await googleTokenValidator.ValidateAsync(
            idToken,
            cancellationToken);

        if (!validation.IsValid || string.IsNullOrWhiteSpace(validation.Subject))
            return (null, validation.ErrorCode ?? ErrorCodes.InvalidGoogleToken);

        var identity = await db.UserIdentities
            .Include(i => i.User)
            .FirstOrDefaultAsync(i =>
                i.Provider == "google" &&
                i.ProviderUserId == validation.Subject,
                cancellationToken);

        var isNewUser = identity is null;
        var now = DateTime.UtcNow;
        if (identity is null)
        {
            var user = new User
            {
                Name = ResolveGoogleName(
                    validation.Name,
                    validation.GivenName,
                    validation.FamilyName,
                    validation.Email),
                Status = "active",
                CreatedAt = now,
                UpdatedAt = now,
            };

            identity = new UserIdentity
            {
                User = user,
                Provider = "google",
                ProviderUserId = validation.Subject,
                ProviderEmail = NormalizeOptionalEmail(validation.Email),
                EmailVerifiedAt = validation.EmailVerified ? now : null,
                CreatedAt = now,
                UpdatedAt = now,
            };

            db.Users.Add(user);
            db.UserIdentities.Add(identity);
        }
        else
        {
            if (identity.User.Status != "active")
                return (null, ErrorCodes.Unauthorized);

            if (string.IsNullOrWhiteSpace(identity.ProviderEmail))
            {
                identity.ProviderEmail = NormalizeOptionalEmail(validation.Email);
            }

            if (identity.EmailVerifiedAt is null && validation.EmailVerified)
            {
                identity.EmailVerifiedAt = now;
            }

            identity.UpdatedAt = now;
        }

        var response = await IssueSessionAsync(identity.User);

        // New Google users get their Google profile photo as a default avatar.
        if (isNewUser && !string.IsNullOrWhiteSpace(validation.Picture))
        {
            await profileService.TrySetAvatarFromUrlAsync(
                identity.User.Id,
                validation.Picture!,
                cancellationToken);
        }

        return (response, string.Empty);
    }

    /// <summary>
    /// Validates a refresh token and returns a fresh access token.
    /// The refresh token itself is NOT rotated — the same token is returned so
    /// clients are never forced to re-login due to a lost-write race condition.
    /// The session expiry is extended on each successful call (rolling window).
    /// Returns null with <see cref="ErrorCodes.SessionExpired"/> if the session is
    /// expired or revoked.
    /// </summary>
    public async Task<(LoginResponse? Response, string ErrorCode)> RefreshAsync(string plainRefreshToken)
    {
        var now = DateTime.UtcNow;
        var lookup = JwtService.ComputeRefreshTokenLookup(plainRefreshToken);

        var session = await db.UserSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s =>
                s.RefreshTokenLookup == lookup &&
                s.RevokedAt == null &&
                s.ExpiresAt > now);

        if (session is not null && !jwt.VerifyRefreshToken(plainRefreshToken, session.RefreshTokenHash))
            session = null;

        if (session is null)
        {
            var legacySessions = await db.UserSessions
                .Include(s => s.User)
                .Where(s =>
                    s.RefreshTokenLookup == null &&
                    s.RevokedAt == null &&
                    s.ExpiresAt > now)
                .ToListAsync();

            session = legacySessions.FirstOrDefault(s =>
                jwt.VerifyRefreshToken(plainRefreshToken, s.RefreshTokenHash));

            // Back-fill the lookup index so future refreshes use the fast path.
            if (session is not null)
                session.RefreshTokenLookup = lookup;
        }

        if (session is null)
            return (null, ErrorCodes.SessionExpired);

        if (session.User.Status != "active")
            return (null, ErrorCodes.SessionExpired);

        // Issue a new access token. The refresh token is intentionally NOT rotated —
        // rotation caused lost-write races where iOS could kill the app after the
        // server committed the new token but before the client saved it, permanently
        // locking the user out until they re-authenticated.
        var accessToken = jwt.GenerateAccessToken(session.UserId, session.User.Role);

        // Extend the session window on each use (rolling 90-day expiry).
        session.ExpiresAt = DateTime.UtcNow.AddDays(_settings.RefreshTokenExpiryDays);
        session.User.LastLoginAt = DateTime.UtcNow;
        session.User.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        return (new LoginResponse(
            AccessToken: accessToken,
            TokenType: "Bearer",
            ExpiresIn: _settings.AccessTokenExpiryMinutes * 60,
            RefreshToken: plainRefreshToken
        ), string.Empty);
    }

    private async Task<LoginResponse> IssueSessionAsync(User user, string? deviceName = null)
    {
        var now = DateTime.UtcNow;
        user.LastLoginAt = now;
        user.UpdatedAt = now;

        // New users still have default(Guid) until inserted; JWT must not use Guid.Empty as `sub`.
        if (user.Id == Guid.Empty)
            await db.SaveChangesAsync();

        var accessToken = jwt.GenerateAccessToken(user.Id, user.Role);
        var plainRefreshToken = jwt.GenerateRefreshToken();

        db.UserSessions.Add(new UserSession
        {
            User = user,
            RefreshTokenHash = jwt.HashRefreshToken(plainRefreshToken),
            RefreshTokenLookup = JwtService.ComputeRefreshTokenLookup(plainRefreshToken),
            DeviceName = deviceName,
            ExpiresAt = now.AddDays(_settings.RefreshTokenExpiryDays),
            CreatedAt = now,
        });

        await db.SaveChangesAsync();

        return new LoginResponse(
            AccessToken: accessToken,
            TokenType: "Bearer",
            ExpiresIn: _settings.AccessTokenExpiryMinutes * 60,
            RefreshToken: plainRefreshToken
        );
    }

    private static string NormalizeEmail(string email)
        => email.Trim().ToLowerInvariant();

    private static string? NormalizeOptionalEmail(string? email)
        => string.IsNullOrWhiteSpace(email) ? null : NormalizeEmail(email);

    private static string DefaultNameFromEmail(string email)
    {
        var atIndex = email.IndexOf('@');
        if (atIndex > 0)
            return email[..atIndex];

        return email;
    }

    private static string ResolveAppleName(string? givenName, string? familyName, string? email)
    {
        var combinedName = string.Join(
            " ",
            new[] { givenName?.Trim(), familyName?.Trim() }
                .Where(x => !string.IsNullOrWhiteSpace(x)));

        if (!string.IsNullOrWhiteSpace(combinedName))
            return combinedName;

        if (!string.IsNullOrWhiteSpace(email))
            return DefaultNameFromEmail(NormalizeEmail(email));

        return "Bantera user";
    }

    private static string ResolveGoogleName(string? name, string? givenName, string? familyName, string? email)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();

        var combinedName = string.Join(
            " ",
            new[] { givenName?.Trim(), familyName?.Trim() }
                .Where(x => !string.IsNullOrWhiteSpace(x)));

        if (!string.IsNullOrWhiteSpace(combinedName))
            return combinedName;

        if (!string.IsNullOrWhiteSpace(email))
            return DefaultNameFromEmail(NormalizeEmail(email));

        return "Bantera user";
    }
}

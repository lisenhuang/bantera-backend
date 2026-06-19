using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BanteraApi.Auth;

public class GoogleIdentityTokenValidator(
    HttpClient httpClient,
    IOptions<GoogleSignInSettings> options,
    ILogger<GoogleIdentityTokenValidator> logger)
{
    private readonly GoogleSignInSettings _settings = options.Value;

    public async Task<GoogleTokenValidationResult> ValidateAsync(
        string idToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            logger.LogWarning("Google ID token is empty.");
            return GoogleTokenValidationResult.Fail(ErrorCodes.InvalidGoogleToken);
        }

        if (string.IsNullOrWhiteSpace(_settings.ClientId))
            throw new InvalidOperationException("GoogleSignIn:ClientId must be configured.");

        var tokenHandler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false,
        };
        if (!tokenHandler.CanReadToken(idToken))
        {
            logger.LogWarning(
                "Google ID token is not a readable JWT. Length={Length}, Prefix={Prefix}",
                idToken.Length,
                SafePrefix(idToken));
            return GoogleTokenValidationResult.Fail(ErrorCodes.InvalidGoogleToken);
        }

        JwtSecurityToken? unvalidatedToken;
        try
        {
            unvalidatedToken = tokenHandler.ReadJwtToken(idToken);
            logger.LogInformation(
                "Google token received. kid={Kid}, alg={Alg}, aud={Audience}, iss={Issuer}, sub_prefix={SubjectPrefix}",
                unvalidatedToken.Header.Kid,
                unvalidatedToken.Header.Alg,
                string.Join(",", unvalidatedToken.Audiences),
                unvalidatedToken.Issuer,
                SafePrefix(unvalidatedToken.Subject));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read Google ID token before validation.");
            return GoogleTokenValidationResult.Fail(ErrorCodes.InvalidGoogleToken);
        }

        IEnumerable<SecurityKey> signingKeys;
        try
        {
            var keysJson = await httpClient.GetStringAsync(_settings.KeysUrl, cancellationToken);
            signingKeys = JsonWebKeySet.Create(keysJson).GetSigningKeys();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download Google signing keys.");
            return GoogleTokenValidationResult.Fail(ErrorCodes.InvalidGoogleToken);
        }

        ClaimsPrincipal principal;
        try
        {
            principal = tokenHandler.ValidateToken(idToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = _settings.ValidIssuers,
                ValidateAudience = true,
                ValidAudiences = [_settings.ClientId],
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = signingKeys,
                ClockSkew = TimeSpan.FromMinutes(1),
            }, out _);
        }
        catch (SecurityTokenInvalidAudienceException ex)
        {
            logger.LogWarning(ex,
                "Google ID token audience validation failed. ExpectedAudience={ExpectedAudience}",
                _settings.ClientId);
            return GoogleTokenValidationResult.Fail(ErrorCodes.GoogleAudienceMismatch);
        }
        catch (SecurityTokenException ex)
        {
            logger.LogWarning(ex, "Google ID token validation failed.");
            return GoogleTokenValidationResult.Fail(ErrorCodes.InvalidGoogleToken);
        }

        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? unvalidatedToken?.Subject;

        if (string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning("Google ID token validated but subject claim was missing.");
            return GoogleTokenValidationResult.Fail(ErrorCodes.InvalidGoogleToken);
        }

        logger.LogInformation(
            "Google ID token validated successfully. sub_prefix={SubjectPrefix}, email_present={HasEmail}, email_verified={EmailVerified}",
            SafePrefix(subject),
            !string.IsNullOrWhiteSpace(principal.FindFirstValue("email")),
            ParseBooleanClaim(principal.FindFirstValue("email_verified")));

        return GoogleTokenValidationResult.Success(
            subject,
            principal.FindFirstValue("email"),
            ParseBooleanClaim(principal.FindFirstValue("email_verified")),
            principal.FindFirstValue("name"),
            principal.FindFirstValue("given_name"),
            principal.FindFirstValue("family_name"),
            principal.FindFirstValue("picture"));
    }

    private static bool ParseBooleanClaim(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal);

    private static string SafePrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";

        return value.Length <= 12 ? value : value[..12];
    }
}

public sealed record GoogleTokenValidationResult(
    bool IsValid,
    string? ErrorCode,
    string? Subject,
    string? Email,
    bool EmailVerified,
    string? Name,
    string? GivenName,
    string? FamilyName,
    string? Picture)
{
    public static GoogleTokenValidationResult Success(
        string subject,
        string? email,
        bool emailVerified,
        string? name,
        string? givenName,
        string? familyName,
        string? picture) =>
        new(true, null, subject, email, emailVerified, name, givenName, familyName, picture);

    public static GoogleTokenValidationResult Fail(string errorCode) =>
        new(false, errorCode, null, null, false, null, null, null, null);
}

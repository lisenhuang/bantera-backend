using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BanteraApi.Auth;

public class AppleIdentityTokenValidator(
    HttpClient httpClient,
    IOptions<AppleSignInSettings> options,
    ILogger<AppleIdentityTokenValidator> logger)
{
    private readonly AppleSignInSettings _settings = options.Value;

    public async Task<AppleTokenValidationResult> ValidateAsync(
        string identityToken,
        string? expectedUserIdentifier,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identityToken))
        {
            logger.LogWarning("Apple identity token is empty.");
            return AppleTokenValidationResult.Fail(ErrorCodes.InvalidAppleToken);
        }

        if (_settings.ValidAudiences.Length == 0)
            throw new InvalidOperationException("AppleSignIn:ValidAudiences must contain at least one allowed audience.");

        JwtSecurityToken? unvalidatedToken = null;
        var tokenHandler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false,
        };
        if (!tokenHandler.CanReadToken(identityToken))
        {
            logger.LogWarning(
                "Apple identity token is not a readable JWT. Length={Length}, Prefix={Prefix}",
                identityToken.Length,
                SafePrefix(identityToken));
            return AppleTokenValidationResult.Fail(ErrorCodes.InvalidAppleToken);
        }

        try
        {
            unvalidatedToken = tokenHandler.ReadJwtToken(identityToken);
            logger.LogInformation(
                "Apple token received. kid={Kid}, alg={Alg}, aud={Audience}, iss={Issuer}, sub_prefix={SubjectPrefix}",
                unvalidatedToken.Header.Kid,
                unvalidatedToken.Header.Alg,
                string.Join(",", unvalidatedToken.Audiences),
                unvalidatedToken.Issuer,
                SafePrefix(unvalidatedToken.Subject));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read Apple identity token before validation.");
            return AppleTokenValidationResult.Fail(ErrorCodes.InvalidAppleToken);
        }

        IEnumerable<SecurityKey> signingKeys;
        try
        {
            var keysJson = await httpClient.GetStringAsync(_settings.KeysUrl, cancellationToken);
            signingKeys = JsonWebKeySet.Create(keysJson).GetSigningKeys();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download Apple signing keys.");
            return AppleTokenValidationResult.Fail(ErrorCodes.InvalidAppleToken);
        }

        ClaimsPrincipal principal;
        try
        {
            principal = tokenHandler.ValidateToken(identityToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _settings.Issuer,
                ValidateAudience = true,
                ValidAudiences = _settings.ValidAudiences,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = signingKeys,
                ClockSkew = TimeSpan.FromMinutes(1),
            }, out _);
        }
        catch (SecurityTokenInvalidAudienceException ex)
        {
            logger.LogWarning(ex,
                "Apple identity token audience validation failed. AllowedAudiences={AllowedAudiences}",
                string.Join(",", _settings.ValidAudiences));
            return AppleTokenValidationResult.Fail(ErrorCodes.AppleAudienceMismatch);
        }
        catch (SecurityTokenException ex)
        {
            logger.LogWarning(ex, "Apple identity token validation failed.");
            return AppleTokenValidationResult.Fail(ErrorCodes.InvalidAppleToken);
        }

        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? unvalidatedToken?.Subject;

        if (string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning("Apple identity token validated but subject claim was missing.");
            return AppleTokenValidationResult.Fail(ErrorCodes.InvalidAppleToken);
        }

        if (!string.IsNullOrWhiteSpace(expectedUserIdentifier) &&
            !string.Equals(subject, expectedUserIdentifier, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Apple identity mismatch. expected_prefix={ExpectedPrefix}, actual_prefix={ActualPrefix}",
                SafePrefix(expectedUserIdentifier),
                SafePrefix(subject));
            return AppleTokenValidationResult.Fail(ErrorCodes.AppleIdentityMismatch);
        }

        logger.LogInformation(
            "Apple identity token validated successfully. sub_prefix={SubjectPrefix}, email_present={HasEmail}, email_verified={EmailVerified}",
            SafePrefix(subject),
            !string.IsNullOrWhiteSpace(principal.FindFirstValue("email")),
            ParseBooleanClaim(principal.FindFirstValue("email_verified")));

        return AppleTokenValidationResult.Success(
            subject,
            principal.FindFirstValue("email"),
            ParseBooleanClaim(principal.FindFirstValue("email_verified")),
            ParseBooleanClaim(principal.FindFirstValue("is_private_email")));
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

public sealed record AppleTokenValidationResult(
    bool IsValid,
    string? ErrorCode,
    string? Subject,
    string? Email,
    bool EmailVerified,
    bool IsPrivateEmail)
{
    public static AppleTokenValidationResult Success(
        string subject,
        string? email,
        bool emailVerified,
        bool isPrivateEmail) =>
        new(true, null, subject, email, emailVerified, isPrivateEmail);

    public static AppleTokenValidationResult Fail(string errorCode) =>
        new(false, errorCode, null, null, false, false);
}

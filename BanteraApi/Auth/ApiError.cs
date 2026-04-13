using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Auth;

/// <summary>Standard error response returned for all non-2xx responses.</summary>
public record ApiError(
    [property: SwaggerSchema("Machine-readable error code. e.g. token_expired")]
    string Code,
    [property: SwaggerSchema("Human-readable description of the error.")]
    string Message
);

/// <summary>Well-known error codes the app should handle.</summary>
public static class ErrorCodes
{
    /// <summary>Unexpected internal server error.</summary>
    public const string InternalError = "internal_error";

    /// <summary>Access token is expired. Call POST /api/auth/refresh.</summary>
    public const string TokenExpired = "token_expired";

    /// <summary>Refresh token is expired or revoked. User must log in again.</summary>
    public const string SessionExpired = "session_expired";

    /// <summary>Credentials are invalid (wrong email or password).</summary>
    public const string InvalidCredentials = "invalid_credentials";

    /// <summary>Email is already registered for the email/password provider.</summary>
    public const string EmailAlreadyRegistered = "email_already_registered";

    /// <summary>Apple identity token could not be validated.</summary>
    public const string InvalidAppleToken = "invalid_apple_token";

    /// <summary>The Apple credential user identifier does not match the signed token subject.</summary>
    public const string AppleIdentityMismatch = "apple_identity_mismatch";

    /// <summary>The Apple identity token audience does not match this app's configured identifier.</summary>
    public const string AppleAudienceMismatch = "apple_audience_mismatch";

    /// <summary>Provided profile data failed validation.</summary>
    public const string InvalidProfile = "invalid_profile";

    /// <summary>Provided profile image failed validation.</summary>
    public const string InvalidProfileImage = "invalid_profile_image";

    /// <summary>Provided video upload data failed validation.</summary>
    public const string InvalidVideoUpload = "invalid_video_upload";

    /// <summary>Request is missing or has an invalid Authorization header.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>User has reached their daily AI audio generation limit.</summary>
    public const string DailyLimitReached = "daily_limit_reached";
}

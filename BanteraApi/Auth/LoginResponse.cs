using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Auth;

public record LoginResponse(
    [property: SwaggerSchema("JWT access token. Valid for 15 minutes. Store in memory only.")]
    string AccessToken,
    [property: SwaggerSchema("Always 'Bearer'.")]
    string TokenType,
    [property: SwaggerSchema("Access token lifetime in seconds. 900 = 15 minutes.")]
    int ExpiresIn,
    [property: SwaggerSchema("Opaque refresh token. Valid for 90 days (rolling). Store in iOS Keychain / Android Keystore.")]
    string RefreshToken
);

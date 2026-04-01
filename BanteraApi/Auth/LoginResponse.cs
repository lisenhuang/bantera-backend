namespace BanteraApi.Auth;

public record LoginResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string RefreshToken
);

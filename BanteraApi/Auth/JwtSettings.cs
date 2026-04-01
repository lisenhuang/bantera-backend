namespace BanteraApi.Auth;

public class JwtSettings
{
    public const string Section = "Jwt";

    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string Secret { get; init; } = string.Empty;
    public int AccessTokenExpiryMinutes { get; init; } = 60;
    public int RefreshTokenExpiryDays { get; init; } = 30;
}

namespace BanteraApi.Auth;

public class AppleSignInSettings
{
    public const string Section = "AppleSignIn";

    public string Issuer { get; init; } = "https://appleid.apple.com";
    public string KeysUrl { get; init; } = "https://appleid.apple.com/auth/keys";
    public string[] ValidAudiences { get; init; } = [];
}

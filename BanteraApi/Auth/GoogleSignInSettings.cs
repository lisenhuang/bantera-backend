namespace BanteraApi.Auth;

public class GoogleSignInSettings
{
    public const string Section = "GoogleSignIn";

    // Google issues ID tokens with either of these issuer strings.
    public string[] ValidIssuers { get; init; } =
        ["https://accounts.google.com", "accounts.google.com"];

    public string KeysUrl { get; init; } = "https://www.googleapis.com/oauth2/v3/certs";

    public string AuthorizationEndpoint { get; init; } =
        "https://accounts.google.com/o/oauth2/v2/auth";

    public string TokenEndpoint { get; init; } = "https://oauth2.googleapis.com/token";

    // The **Web application** OAuth client. The whole browser flow runs server-side,
    // so the secret never leaves the backend. ClientId is also the token audience.
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";

    // Backend callback Google redirects to. MUST exactly match an "Authorized
    // redirect URI" on the Web client, e.g. https://api.bantera.app/api/auth/google/callback
    public string RedirectUri { get; init; } = "";

    // Custom scheme the backend uses to hand the result back to the app (Android).
    public string AppRedirectUri { get; init; } = "bantera://google-auth";
}

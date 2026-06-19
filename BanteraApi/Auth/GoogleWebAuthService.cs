using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BanteraApi.Auth;

/// <summary>
/// Server-mediated Google OAuth 2.0 (Authorization Code + PKCE) flow.
///
/// The Android app opens <c>/api/auth/google/start</c> in a Custom Tab; the whole
/// browser exchange runs here so the client secret never leaves the backend, and
/// no app SHA-1 / Android OAuth client is required. The result is handed back to
/// the app as a short-lived one-time code on a custom-scheme deep link, which the
/// app redeems at <c>/api/auth/google/exchange</c> for the Bantera token pair.
///
/// Short-lived flow state is kept in <see cref="IMemoryCache"/>. If the API is
/// ever scaled to multiple instances, swap this for a distributed cache.
/// </summary>
public class GoogleWebAuthService(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptions<GoogleSignInSettings> options,
    AuthService authService,
    ILogger<GoogleWebAuthService> logger)
{
    private readonly GoogleSignInSettings _settings = options.Value;

    private static readonly TimeSpan FlowTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(2);

    public string BuildAuthorizationUrl()
    {
        if (string.IsNullOrWhiteSpace(_settings.ClientId) || string.IsNullOrWhiteSpace(_settings.RedirectUri))
            throw new InvalidOperationException("GoogleSignIn:ClientId and RedirectUri must be configured.");

        var state = RandomToken(32);
        var codeVerifier = RandomToken(48);
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        cache.Set(StateKey(state), codeVerifier, FlowTtl);

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _settings.ClientId,
            ["redirect_uri"] = _settings.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        };

        return QueryHelpers.AddQueryString(_settings.AuthorizationEndpoint, query);
    }

    /// <summary>Returns the custom-scheme URL the browser should be redirected to.</summary>
    public async Task<string> HandleCallbackAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
        {
            logger.LogWarning("Google callback returned error: {Error}", error);
            return AppRedirect("error", "google_denied");
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return AppRedirect("error", "google_invalid");

        if (!cache.TryGetValue(StateKey(state), out string? codeVerifier) || string.IsNullOrEmpty(codeVerifier))
        {
            logger.LogWarning("Google callback state not found or expired.");
            return AppRedirect("error", "google_expired");
        }
        cache.Remove(StateKey(state));

        var idToken = await ExchangeCodeForIdTokenAsync(code, codeVerifier, ct);
        if (idToken is null)
            return AppRedirect("error", "google_exchange_failed");

        var (response, errorCode) = await authService.LoginWithGoogleAsync(idToken, ct);
        if (response is null)
        {
            logger.LogWarning("Google login failed after token exchange: {Code}", errorCode);
            return AppRedirect("error", "google_login_failed");
        }

        var oneTime = RandomToken(32);
        cache.Set(CodeKey(oneTime), response, CodeTtl);
        return AppRedirect("code", oneTime);
    }

    /// <summary>Single-use redemption of the one-time code for the Bantera token pair.</summary>
    public LoginResponse? Redeem(string? oneTimeCode)
    {
        if (string.IsNullOrWhiteSpace(oneTimeCode))
            return null;

        if (cache.TryGetValue(CodeKey(oneTimeCode), out LoginResponse? response) && response is not null)
        {
            cache.Remove(CodeKey(oneTimeCode));
            return response;
        }

        return null;
    }

    private async Task<string?> ExchangeCodeForIdTokenAsync(string code, string codeVerifier, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _settings.ClientId,
                ["client_secret"] = _settings.ClientSecret,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = _settings.RedirectUri,
            });

            using var resp = await httpClient.PostAsync(_settings.TokenEndpoint, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Google token exchange failed with status {Status}.", resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id_token", out var idTokenEl)
                ? idTokenEl.GetString()
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Google token exchange threw.");
            return null;
        }
    }

    private string AppRedirect(string key, string value)
        => QueryHelpers.AddQueryString(_settings.AppRedirectUri, key, value);

    /// <summary>
    /// Wraps the app deep-link in a tiny HTML page served from our own domain. The page
    /// immediately bounces to the <c>bantera://</c> deep link — which is what completes the
    /// native flutter_web_auth_2 flow and closes the Auth Tab — and, as a fallback if the
    /// automatic hand-off is ever blocked, shows a friendly "you can close this tab" message
    /// plus a manual button. A browser tab generally cannot close itself via JS, so the
    /// native deep-link return is the real close mechanism; this page is graceful UX only.
    /// </summary>
    public static string BuildCallbackHtml(string appUrl)
    {
        var hrefAttr = System.Text.Encodings.Web.HtmlEncoder.Default.Encode(appUrl);
        var jsString = JsonSerializer.Serialize(appUrl);
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="refresh" content="0;url={{hrefAttr}}">
            <title>Bantera</title>
            <style>
              html,body{height:100%;margin:0}
              body{display:flex;align-items:center;justify-content:center;
                font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif;
                background:#0f1115;color:#f5f5f7;text-align:center;padding:24px}
              .card{max-width:340px}
              h1{font-size:20px;margin:0 0 8px}
              p{font-size:15px;line-height:1.5;color:#b8b8c0;margin:0 0 20px}
              a.btn{display:inline-block;padding:12px 22px;border-radius:999px;
                background:#6c5ce7;color:#fff;text-decoration:none;font-weight:600;font-size:15px}
            </style>
            </head>
            <body>
              <div class="card">
                <h1>✅ You're signed in</h1>
                <p>You can close this tab and return to Bantera.</p>
                <a class="btn" href="{{hrefAttr}}">Open Bantera</a>
              </div>
              <script>
                // Hand control back to the app. This navigation to the bantera:// scheme is
                // what flutter_web_auth_2 intercepts to finish login and close the tab.
                window.location.replace({{jsString}});
                // Best effort only — an Auth/Custom Tab usually cannot be closed by its own JS.
                setTimeout(function(){ try { window.close(); } catch (e) {} }, 1500);
              </script>
            </body>
            </html>
            """;
    }

    private static string StateKey(string state) => $"google_oauth_state:{state}";
    private static string CodeKey(string code) => $"google_oauth_code:{code}";

    private static string RandomToken(int bytes)
        => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

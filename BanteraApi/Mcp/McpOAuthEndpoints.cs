using System.Text;
using BanteraApi.Auth;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BanteraApi.Mcp;

/// <summary>
/// OAuth 2.1 authorization-server endpoints for the MCP resource server, plus the two
/// admin-authenticated endpoints the website consent page calls.
///
/// The MCP client (Claude) talks to /.well-known/oauth-authorization-server, /oauth/register,
/// /oauth/authorize, /oauth/token and /oauth/revoke. The protected-resource metadata document
/// is served by the MCP authentication handler, not here.
/// </summary>
public static class McpOAuthEndpoints
{
    private const int MaxFormBytes = 8 * 1024;

    public static void Map(WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<McpSettings>>().Value;

        MapMetadata(app, settings);
        MapRegistration(app);
        MapAuthorize(app, settings);
        MapToken(app, settings);
        MapRevoke(app);
        MapConsentApi(app);
    }

    // ── RFC 8414 authorization server metadata ────────────────────────────────
    private static void MapMetadata(WebApplication app, McpSettings settings)
    {
        app.MapGet("/.well-known/oauth-authorization-server", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Ok(new Dictionary<string, object?>
            {
                ["issuer"] = settings.Issuer,
                ["authorization_endpoint"] = $"{settings.Issuer}/oauth/authorize",
                ["token_endpoint"] = $"{settings.Issuer}/oauth/token",
                ["registration_endpoint"] = $"{settings.Issuer}/oauth/register",
                ["revocation_endpoint"] = $"{settings.Issuer}/oauth/revoke",
                ["response_types_supported"] = new[] { "code" },
                ["response_modes_supported"] = new[] { "query" },
                ["grant_types_supported"] = OAuthGrantTypes.Supported,
                ["code_challenge_methods_supported"] = new[] { Pkce.S256 },
                ["token_endpoint_auth_methods_supported"] = OAuthAuthMethods.Supported,
                ["revocation_endpoint_auth_methods_supported"] = new[] { OAuthAuthMethods.None, OAuthAuthMethods.ClientSecretPost },
                ["scopes_supported"] = McpScopes.All,
            });
        })
        .AllowAnonymous()
        .WithName("McpOAuthServerMetadata")
        .ExcludeFromDescription();
    }

    // ── RFC 7591 dynamic client registration ──────────────────────────────────
    private static void MapRegistration(WebApplication app)
    {
        app.MapPost("/oauth/register", async (
            HttpContext ctx,
            [FromBody] ClientRegistrationRequest request,
            OAuthClientStore store,
            CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";

            var (response, error) = await store.RegisterDynamicAsync(request, ct);
            return error is not null
                ? Results.Json(error, statusCode: StatusCodes.Status400BadRequest)
                : Results.Json(response, statusCode: StatusCodes.Status201Created);
        })
        .AllowAnonymous()
        .RequireRateLimiting("oauth-register")
        .WithName("McpOAuthRegister")
        .ExcludeFromDescription();
    }

    // ── Authorization endpoint ────────────────────────────────────────────────
    private static void MapAuthorize(WebApplication app, McpSettings settings)
    {
        app.MapGet("/oauth/authorize", async (
            HttpContext ctx,
            OAuthClientStore store,
            AuthCodeService codes,
            [FromQuery] string? client_id,
            [FromQuery] string? redirect_uri,
            [FromQuery] string? response_type,
            [FromQuery] string? code_challenge,
            [FromQuery] string? code_challenge_method,
            [FromQuery] string? state,
            [FromQuery] string? scope,
            [FromQuery] string? resource,
            CancellationToken ct) =>
        {
            // Phase A — never redirect until the client and redirect URI are verified.
            var client = await store.FindByClientIdAsync(client_id, ct);
            if (client is null)
                return InvalidClientPage("Unknown or revoked client_id.");

            var matched = OAuthRedirectUri.FindMatch(client.RedirectUris, redirect_uri);
            if (matched is null)
                return InvalidClientPage("The redirect_uri does not match any URI registered for this client.");

            // Phase B — the redirect target is trusted, so errors go back to the client.
            if (!string.Equals(response_type, "code", StringComparison.Ordinal))
            {
                return Results.Redirect(OAuthErrors.BuildErrorRedirect(
                    redirect_uri!, OAuthErrors.UnsupportedResponseType, "Only the code response type is supported.", state));
            }

            if (!string.Equals(code_challenge_method, Pkce.S256, StringComparison.Ordinal)
                || !Pkce.IsValidChallenge(code_challenge))
            {
                return Results.Redirect(OAuthErrors.BuildErrorRedirect(
                    redirect_uri!, OAuthErrors.InvalidRequest, "A valid S256 code_challenge is required.", state));
            }

            if (state is { Length: > 1024 })
            {
                return Results.Redirect(OAuthErrors.BuildErrorRedirect(
                    redirect_uri!, OAuthErrors.InvalidRequest, "state is too long.", state: null));
            }

            if (!McpScopes.TryParse(scope, out var requestedScopes))
            {
                return Results.Redirect(OAuthErrors.BuildErrorRedirect(
                    redirect_uri!, OAuthErrors.InvalidScope, "Unknown scope requested.", state));
            }

            if (requestedScopes.Count == 0)
                requestedScopes.Add(McpScopes.Read);

            if (!string.IsNullOrEmpty(resource) && !codes.ResourceMatches(resource))
            {
                return Results.Redirect(OAuthErrors.BuildErrorRedirect(
                    redirect_uri!, OAuthErrors.InvalidTarget, "The requested resource is not valid.", state));
            }

            var requestId = await codes.CreatePendingAsync(
                client,
                matched is not null && OAuthRedirectUri.Matches(matched, redirect_uri!) ? redirect_uri! : matched!,
                McpScopes.Join(requestedScopes),
                code_challenge!,
                state,
                settings.ResourceUrl,
                ct);

            var separator = settings.ConsentUrl.Contains('?') ? '&' : '?';
            return Results.Redirect($"{settings.ConsentUrl}{separator}request_id={requestId}");
        })
        .AllowAnonymous()
        .RequireRateLimiting("oauth-authorize")
        .WithName("McpOAuthAuthorize")
        .ExcludeFromDescription();
    }

    // ── Token endpoint ────────────────────────────────────────────────────────
    private static void MapToken(WebApplication app, McpSettings settings)
    {
        app.MapPost("/oauth/token", async (
            HttpContext ctx,
            AppDbContext db,
            OAuthClientStore store,
            AuthCodeService codes,
            OAuthRefreshTokenService refreshTokens,
            McpTokenService tokens,
            CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";

            if (!ctx.Request.HasFormContentType)
            {
                return Results.Json(
                    new OAuthError(OAuthErrors.InvalidRequest, "Content-Type must be application/x-www-form-urlencoded."),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (ctx.Request.ContentLength > MaxFormBytes)
            {
                return Results.Json(
                    new OAuthError(OAuthErrors.InvalidRequest, "Request body is too large."),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var form = await ctx.Request.ReadFormAsync(ct);

            var (clientId, clientSecret) = ReadClientCredentials(ctx, form);
            var client = await store.FindByClientIdAsync(clientId, ct);
            if (client is null)
                return UnauthorizedClient("Unknown or revoked client.");

            if (!OAuthClientStore.VerifySecret(client, clientSecret))
                return UnauthorizedClient("Client authentication failed.");

            var grantType = form["grant_type"].ToString();

            return grantType switch
            {
                OAuthGrantTypes.AuthorizationCode =>
                    await HandleAuthorizationCodeAsync(db, codes, refreshTokens, tokens, client, form, ct),
                OAuthGrantTypes.RefreshToken =>
                    await HandleRefreshTokenAsync(db, refreshTokens, tokens, client, form, ct),
                _ => Results.Json(
                    new OAuthError(OAuthErrors.UnsupportedGrantType, "Unsupported grant_type."),
                    statusCode: StatusCodes.Status400BadRequest),
            };
        })
        .AllowAnonymous()
        .RequireRateLimiting("oauth-token")
        .WithName("McpOAuthToken")
        .ExcludeFromDescription();
    }

    private static async Task<IResult> HandleAuthorizationCodeAsync(
        AppDbContext db,
        AuthCodeService codes,
        OAuthRefreshTokenService refreshTokens,
        McpTokenService tokens,
        OAuthClient client,
        IFormCollection form,
        CancellationToken ct)
    {
        var (row, error) = await codes.RedeemAsync(
            form["code"].ToString(),
            client,
            form["redirect_uri"].ToString(),
            form["code_verifier"].ToString(),
            form["resource"].ToString(),
            ct);

        if (error is not null)
            return Results.Json(error, statusCode: StatusCodes.Status400BadRequest);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == row!.UserId, ct);
        if (!IsEligibleAdmin(user))
            return Results.Json(new OAuthError(OAuthErrors.InvalidGrant, "The account is no longer an active administrator."),
                statusCode: StatusCodes.Status400BadRequest);

        var scopes = (row!.GrantedScopes ?? McpScopes.Read).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var accessToken = tokens.IssueAccessToken(user!.Id, user.Role, scopes, client.ClientId);
        var refreshToken = await refreshTokens.IssueAsync(client.Id, user.Id, scopes, familyId: null, row.Id, ct);

        client.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new OAuthTokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = tokens.AccessTokenSeconds,
            RefreshToken = refreshToken,
            Scope = McpScopes.Join(scopes),
        });
    }

    private static async Task<IResult> HandleRefreshTokenAsync(
        AppDbContext db,
        OAuthRefreshTokenService refreshTokens,
        McpTokenService tokens,
        OAuthClient client,
        IFormCollection form,
        CancellationToken ct)
    {
        var (newToken, old, error) = await refreshTokens.RotateAsync(form["refresh_token"].ToString(), client, ct);
        if (error is not null)
            return Results.Json(error, statusCode: StatusCodes.Status400BadRequest);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == old!.UserId, ct);
        if (!IsEligibleAdmin(user))
        {
            await refreshTokens.RevokeFamilyAsync(old!.FamilyId, ct);
            return Results.Json(new OAuthError(OAuthErrors.InvalidGrant, "The account is no longer an active administrator."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var granted = old!.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // An explicit scope on refresh may only narrow what was already granted.
        var requestedScope = form["scope"].ToString();
        if (!string.IsNullOrWhiteSpace(requestedScope))
        {
            if (!McpScopes.TryParse(requestedScope, out var requested) || !McpScopes.IsSubset(requested, granted))
                return Results.Json(new OAuthError(OAuthErrors.InvalidScope, "Requested scope exceeds the original grant."),
                    statusCode: StatusCodes.Status400BadRequest);

            granted = [.. requested];
        }

        var accessToken = tokens.IssueAccessToken(user!.Id, user.Role, granted, client.ClientId);

        client.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new OAuthTokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = tokens.AccessTokenSeconds,
            RefreshToken = newToken,
            Scope = McpScopes.Join(granted),
        });
    }

    // ── RFC 7009 revocation ───────────────────────────────────────────────────
    private static void MapRevoke(WebApplication app)
    {
        app.MapPost("/oauth/revoke", async (
            HttpContext ctx,
            OAuthClientStore store,
            OAuthRefreshTokenService refreshTokens,
            CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";

            if (!ctx.Request.HasFormContentType)
                return Results.Ok(new { });

            var form = await ctx.Request.ReadFormAsync(ct);
            var (clientId, clientSecret) = ReadClientCredentials(ctx, form);
            var client = await store.FindByClientIdAsync(clientId, ct);

            // RFC 7009: always return 200, regardless of whether the token existed.
            if (client is not null && OAuthClientStore.VerifySecret(client, clientSecret))
                await refreshTokens.RevokeByTokenAsync(form["token"].ToString(), client, ct);

            return Results.Ok(new { });
        })
        .AllowAnonymous()
        .RequireRateLimiting("oauth-token")
        .WithName("McpOAuthRevoke")
        .ExcludeFromDescription();
    }

    // ── Admin-authenticated consent API (called by the website) ───────────────
    private static void MapConsentApi(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/oauth").RequireAuthorization("Admin");

        group.MapGet("/requests/{requestId:guid}", async (
            Guid requestId,
            AuthCodeService codes,
            CancellationToken ct) =>
        {
            var view = await codes.GetPendingForConsentAsync(requestId, ct);
            return view is null
                ? Results.Json(new ApiError(ErrorCodes.OAuthRequestNotFound,
                    "This authorization request has expired or was already used."), statusCode: 404)
                : Results.Ok(view);
        })
        .WithName("McpOAuthGetConsentRequest");

        group.MapPost("/consent", async (
            [FromBody] ConsentDecisionRequest body,
            System.Security.Claims.ClaimsPrincipal principal,
            AuthCodeService codes,
            CancellationToken ct) =>
        {
            var adminUserId = TryGetUserId(principal);
            if (adminUserId is null)
                return Results.Json(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."), statusCode: 401);

            var (redirectUrl, error) = body.Approve
                ? await codes.ApproveAsync(body.RequestId, adminUserId.Value, body.Scopes ?? [], ct)
                : await codes.DenyAsync(body.RequestId, ct);

            return error switch
            {
                null => Results.Ok(new { redirectUrl }),
                "invalid_scope" => Results.Json(
                    new ApiError(ErrorCodes.InvalidProfile, "The selected permissions exceed what the application requested."),
                    statusCode: 400),
                _ => Results.Json(
                    new ApiError(ErrorCodes.OAuthRequestNotFound, "This authorization request has expired or was already used."),
                    statusCode: 404),
            };
        })
        .WithName("McpOAuthSubmitConsent");

        // Connected apps: list and revoke this admin's MCP grants.
        group.MapGet("/grants", async (
            System.Security.Claims.ClaimsPrincipal principal,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var adminUserId = TryGetUserId(principal);
            if (adminUserId is null)
                return Results.Json(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."), statusCode: 401);

            var grants = await db.OAuthRefreshTokens
                .Where(t => t.UserId == adminUserId.Value && t.RevokedAt == null)
                .Include(t => t.Client)
                .GroupBy(t => t.FamilyId)
                .Select(g => new
                {
                    familyId = g.Key,
                    clientName = g.Max(t => t.Client!.ClientName),
                    clientId = g.Max(t => t.Client!.ClientId),
                    scopes = g.Max(t => t.Scopes),
                    createdAt = g.Min(t => t.CreatedAt),
                    lastUsedAt = g.Max(t => t.LastUsedAt),
                    expiresAt = g.Max(t => t.ExpiresAt),
                })
                .OrderByDescending(g => g.createdAt)
                .ToListAsync(ct);

            return Results.Ok(grants);
        })
        .WithName("McpOAuthListGrants");

        group.MapDelete("/grants/{familyId:guid}", async (
            Guid familyId,
            System.Security.Claims.ClaimsPrincipal principal,
            AppDbContext db,
            OAuthRefreshTokenService refreshTokens,
            CancellationToken ct) =>
        {
            var adminUserId = TryGetUserId(principal);
            if (adminUserId is null)
                return Results.Json(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."), statusCode: 401);

            var owned = await db.OAuthRefreshTokens
                .AnyAsync(t => t.FamilyId == familyId && t.UserId == adminUserId.Value, ct);

            if (!owned)
                return Results.Json(new ApiError(ErrorCodes.OAuthRequestNotFound, "Authorization not found."), statusCode: 404);

            await refreshTokens.RevokeFamilyAsync(familyId, ct);
            return Results.Ok(new { revoked = true });
        })
        .WithName("McpOAuthRevokeGrant");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public sealed class ConsentDecisionRequest
    {
        public Guid RequestId { get; set; }
        public bool Approve { get; set; }
        public List<string>? Scopes { get; set; }
    }

    private static bool IsEligibleAdmin(User? user)
        => user is not null
        && user.DeletedAt is null
        && user.Role == "admin"
        && user.Status == "active";

    /// <summary>Reads client credentials from the form body or a Basic authorization header.</summary>
    private static (string? ClientId, string? ClientSecret) ReadClientCredentials(HttpContext ctx, IFormCollection form)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var separator = decoded.IndexOf(':');
                if (separator > 0)
                {
                    return (Uri.UnescapeDataString(decoded[..separator]),
                            Uri.UnescapeDataString(decoded[(separator + 1)..]));
                }
            }
            catch (FormatException)
            {
                // Fall through to the form fields.
            }
        }

        var clientId = form["client_id"].ToString();
        var clientSecret = form["client_secret"].ToString();
        return (string.IsNullOrEmpty(clientId) ? null : clientId,
                string.IsNullOrEmpty(clientSecret) ? null : clientSecret);
    }

    private static IResult UnauthorizedClient(string description)
        => Results.Json(new OAuthError(OAuthErrors.InvalidClient, description), statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Shown when the client_id or redirect_uri cannot be trusted. Deliberately not a redirect:
    /// sending the user to an unverified URI would be an open redirect.
    /// </summary>
    private static IResult InvalidClientPage(string description)
        => Results.Content($"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>Authorization error</title></head>
            <body style="font-family:system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem">
              <h1 style="font-size:1.25rem">Authorization request rejected</h1>
              <p>{System.Net.WebUtility.HtmlEncode(description)}</p>
              <p style="color:#666;font-size:.875rem">Close this window and try connecting again.</p>
            </body></html>
            """, "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);

    private static Guid? TryGetUserId(System.Security.Claims.ClaimsPrincipal user)
    {
        var raw = user.FindFirst("sub")?.Value
            ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(raw, out var id) && id != Guid.Empty ? id : null;
    }
}

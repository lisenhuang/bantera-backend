using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BanteraApi.Mcp;

/// <summary>What the consent page shows the admin.</summary>
public sealed record ConsentView(
    Guid RequestId,
    string ClientName,
    string ClientId,
    string RedirectUri,
    string RedirectHost,
    bool IsLoopback,
    IReadOnlyList<string> RequestedScopes,
    DateTime ExpiresAt);

/// <summary>
/// Owns the lifecycle of an authorization request: pending → consented (code issued) → redeemed.
/// All three states live in one <see cref="OAuthAuthorizationCode"/> row, so the code is
/// structurally bound to its client, redirect URI, resource, PKCE challenge and admin.
/// </summary>
public class AuthCodeService(
    AppDbContext db,
    IOptions<McpSettings> options,
    ILogger<AuthCodeService> logger)
{
    private readonly McpSettings _settings = options.Value;

    /// <summary>Creates the pending request row; its id is the opaque request_id for the consent page.</summary>
    public async Task<Guid> CreatePendingAsync(
        OAuthClient client,
        string redirectUri,
        string requestedScopes,
        string codeChallenge,
        string? state,
        string resource,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var row = new OAuthAuthorizationCode
        {
            OAuthClientId = client.Id,
            RedirectUri = redirectUri,
            Resource = resource,
            RequestedScopes = requestedScopes,
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = Pkce.S256,
            State = state,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(_settings.AuthorizationCodeMinutes),
        };

        db.OAuthAuthorizationCodes.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    /// <summary>Loads a request that is still awaiting consent, or null if it is spent or expired.</summary>
    public async Task<ConsentView?> GetPendingForConsentAsync(Guid requestId, CancellationToken ct = default)
    {
        var row = await db.OAuthAuthorizationCodes
            .Include(r => r.Client)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (row?.Client is null) return null;
        if (row.ConsentedAt is not null || row.DeniedAt is not null || row.RedeemedAt is not null) return null;
        if (row.ExpiresAt <= DateTime.UtcNow) return null;

        var isLoopback = Uri.TryCreate(row.RedirectUri, UriKind.Absolute, out var uri) && OAuthRedirectUri.IsLoopback(uri);

        return new ConsentView(
            row.Id,
            row.Client.ClientName,
            row.Client.ClientId,
            row.RedirectUri,
            OAuthRedirectUri.DisplayHost(row.RedirectUri),
            isLoopback,
            row.RequestedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            row.ExpiresAt);
    }

    /// <summary>
    /// Approves a pending request: mints the authorization code, binds it to the admin and
    /// the granted scopes, and returns the URL the browser should be sent to.
    /// </summary>
    public async Task<(string? RedirectUrl, string? Error)> ApproveAsync(
        Guid requestId,
        Guid adminUserId,
        IEnumerable<string> grantedScopes,
        CancellationToken ct = default)
    {
        var row = await db.OAuthAuthorizationCodes.FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (row is null) return (null, "not_found");
        if (row.ConsentedAt is not null || row.DeniedAt is not null || row.RedeemedAt is not null) return (null, "not_found");
        if (row.ExpiresAt <= DateTime.UtcNow) return (null, "not_found");

        var requested = row.RequestedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var granted = McpScopes.Normalize(grantedScopes);

        // The admin can narrow, never widen, what the client asked for.
        if (!McpScopes.IsSubset(granted, requested))
            return (null, "invalid_scope");

        var now = DateTime.UtcNow;
        var code = McpTokenService.GenerateAuthorizationCode();

        row.CodeHash = McpTokenService.Lookup(code);
        row.UserId = adminUserId;
        row.GrantedScopes = McpScopes.Join(granted);
        row.ConsentedAt = now;
        row.ExpiresAt = now.AddMinutes(_settings.AuthorizationCodeMinutes);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[MCP OAuth] Admin {AdminUserId} approved request {RequestId} with scopes {Scopes}",
            adminUserId, requestId, row.GrantedScopes);

        return (OAuthErrors.BuildCodeRedirect(row.RedirectUri, code, row.State, _settings.Issuer), null);
    }

    /// <summary>Denies a pending request and returns the error redirect.</summary>
    public async Task<(string? RedirectUrl, string? Error)> DenyAsync(Guid requestId, CancellationToken ct = default)
    {
        var row = await db.OAuthAuthorizationCodes.FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (row is null) return (null, "not_found");
        if (row.ConsentedAt is not null || row.DeniedAt is not null || row.RedeemedAt is not null) return (null, "not_found");

        row.DeniedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return (OAuthErrors.BuildErrorRedirect(
            row.RedirectUri,
            OAuthErrors.AccessDenied,
            "The administrator denied the request.",
            row.State), null);
    }

    /// <summary>
    /// Redeems an authorization code at the token endpoint. Validates every binding and
    /// marks the row consumed atomically so a code can only ever be used once.
    /// </summary>
    public async Task<(OAuthAuthorizationCode? Row, OAuthError? Error)> RedeemAsync(
        string? code,
        OAuthClient client,
        string? redirectUri,
        string? codeVerifier,
        string? resource,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return (null, new OAuthError(OAuthErrors.InvalidRequest, "code is required."));

        var lookup = McpTokenService.Lookup(code);
        var row = await db.OAuthAuthorizationCodes.FirstOrDefaultAsync(r => r.CodeHash == lookup, ct);

        if (row is null)
            return (null, new OAuthError(OAuthErrors.InvalidGrant, "The authorization code is invalid."));

        // Replay: the code was already exchanged. Treat as theft and revoke everything
        // that was issued from it (RFC 6749 §4.1.2).
        if (row.RedeemedAt is not null)
        {
            await RevokeTokensFromCodeAsync(row.Id, ct);
            logger.LogWarning("[MCP OAuth] Authorization code replay detected for request {RequestId}", row.Id);
            return (null, new OAuthError(OAuthErrors.InvalidGrant, "The authorization code has already been used."));
        }

        if (row.OAuthClientId != client.Id)
            return (null, new OAuthError(OAuthErrors.InvalidGrant, "The authorization code was issued to another client."));

        if (row.ConsentedAt is null || row.UserId is null)
            return (null, new OAuthError(OAuthErrors.InvalidGrant, "The authorization code is invalid."));

        if (row.ExpiresAt <= DateTime.UtcNow)
            return (null, new OAuthError(OAuthErrors.InvalidGrant, "The authorization code has expired."));

        if (!string.Equals(row.RedirectUri, redirectUri, StringComparison.Ordinal))
            return (null, new OAuthError(OAuthErrors.InvalidGrant, "redirect_uri does not match the authorization request."));

        if (!string.IsNullOrEmpty(resource) && !ResourceMatches(resource))
            return (null, new OAuthError(OAuthErrors.InvalidTarget, "The requested resource is not valid."));

        if (!Pkce.VerifyS256(codeVerifier, row.CodeChallenge))
            return (null, new OAuthError(OAuthErrors.InvalidGrant, "PKCE verification failed."));

        // Consume the row atomically: only the request that flips RedeemedAt wins.
        var affected = await db.OAuthAuthorizationCodes
            .Where(r => r.Id == row.Id && r.RedeemedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RedeemedAt, DateTime.UtcNow), ct);

        if (affected != 1)
            return (null, new OAuthError(OAuthErrors.InvalidGrant, "The authorization code has already been used."));

        return (row, null);
    }

    /// <summary>True when a supplied resource indicator matches this server's canonical URL.</summary>
    public bool ResourceMatches(string resource)
    {
        static string Trim(string s) => s.TrimEnd('/');
        return string.Equals(Trim(resource), Trim(_settings.ResourceUrl), StringComparison.OrdinalIgnoreCase);
    }

    private async Task RevokeTokensFromCodeAsync(Guid authorizationCodeId, CancellationToken ct)
    {
        var families = await db.OAuthRefreshTokens
            .Where(t => t.AuthorizationCodeId == authorizationCodeId)
            .Select(t => t.FamilyId)
            .Distinct()
            .ToListAsync(ct);

        if (families.Count == 0) return;

        await db.OAuthRefreshTokens
            .Where(t => families.Contains(t.FamilyId) && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);
    }
}

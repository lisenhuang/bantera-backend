using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BanteraApi.Mcp;

/// <summary>
/// Issues and rotates refresh tokens. Every use rotates: the presented token is revoked and
/// replaced. Presenting an already-revoked token means it leaked, so the whole rotation
/// family is revoked (OAuth 2.1 §4.3.1 / MCP spec token-theft guidance).
/// </summary>
public class OAuthRefreshTokenService(
    AppDbContext db,
    IOptions<McpSettings> options,
    ILogger<OAuthRefreshTokenService> logger)
{
    private readonly McpSettings _settings = options.Value;

    public async Task<string> IssueAsync(
        Guid clientId,
        Guid userId,
        IEnumerable<string> scopes,
        Guid? familyId = null,
        Guid? authorizationCodeId = null,
        CancellationToken ct = default)
    {
        var plain = McpTokenService.GenerateRefreshToken();
        var now = DateTime.UtcNow;

        db.OAuthRefreshTokens.Add(new OAuthRefreshToken
        {
            TokenLookup = McpTokenService.Lookup(plain),
            FamilyId = familyId ?? Guid.NewGuid(),
            OAuthClientId = clientId,
            UserId = userId,
            AuthorizationCodeId = authorizationCodeId,
            Scopes = McpScopes.Join(scopes),
            CreatedAt = now,
            ExpiresAt = now.AddDays(_settings.RefreshTokenDays),
        });

        await db.SaveChangesAsync(ct);
        return plain;
    }

    /// <summary>
    /// Validates and rotates a refresh token. On success returns the replacement token and
    /// the row it came from (for user and scope information).
    /// </summary>
    public async Task<(string? NewToken, OAuthRefreshToken? Old, OAuthError? Error)> RotateAsync(
        string? presented,
        OAuthClient client,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presented))
            return (null, null, new OAuthError(OAuthErrors.InvalidRequest, "refresh_token is required."));

        var lookup = McpTokenService.Lookup(presented);
        var row = await db.OAuthRefreshTokens.FirstOrDefaultAsync(t => t.TokenLookup == lookup, ct);

        if (row is null)
            return (null, null, new OAuthError(OAuthErrors.InvalidGrant, "The refresh token is invalid."));

        if (row.OAuthClientId != client.Id)
            return (null, null, new OAuthError(OAuthErrors.InvalidGrant, "The refresh token was issued to another client."));

        // Reuse of a rotated-away token: revoke the entire family.
        if (row.RevokedAt is not null)
        {
            await RevokeFamilyAsync(row.FamilyId, ct);
            logger.LogWarning(
                "[MCP OAuth] Refresh token reuse detected; revoked family {FamilyId} for user {UserId}",
                row.FamilyId, row.UserId);
            return (null, null, new OAuthError(OAuthErrors.InvalidGrant, "The refresh token is no longer valid."));
        }

        if (row.ExpiresAt <= DateTime.UtcNow)
            return (null, null, new OAuthError(OAuthErrors.InvalidGrant, "The refresh token has expired."));

        var now = DateTime.UtcNow;
        var plain = McpTokenService.GenerateRefreshToken();

        var replacement = new OAuthRefreshToken
        {
            TokenLookup = McpTokenService.Lookup(plain),
            FamilyId = row.FamilyId,
            OAuthClientId = row.OAuthClientId,
            UserId = row.UserId,
            AuthorizationCodeId = row.AuthorizationCodeId,
            Scopes = row.Scopes,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_settings.RefreshTokenDays),
        };

        db.OAuthRefreshTokens.Add(replacement);
        row.RevokedAt = now;
        row.LastUsedAt = now;
        await db.SaveChangesAsync(ct);

        row.ReplacedByTokenId = replacement.Id;
        await db.SaveChangesAsync(ct);

        return (plain, row, null);
    }

    public Task<int> RevokeFamilyAsync(Guid familyId, CancellationToken ct = default)
        => db.OAuthRefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);

    /// <summary>Revokes by plaintext token, for the RFC 7009 revocation endpoint.</summary>
    public async Task RevokeByTokenAsync(string? presented, OAuthClient client, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presented)) return;

        var lookup = McpTokenService.Lookup(presented);
        var row = await db.OAuthRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenLookup == lookup && t.OAuthClientId == client.Id, ct);

        if (row is not null)
            await RevokeFamilyAsync(row.FamilyId, ct);
    }
}

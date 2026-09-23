using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BanteraApi.Mcp;

/// <summary>Registration and lookup of OAuth clients for the MCP authorization server.</summary>
public class OAuthClientStore(
    AppDbContext db,
    IOptions<McpSettings> options,
    ILogger<OAuthClientStore> logger)
{
    private readonly McpSettings _settings = options.Value;

    public Task<OAuthClient?> FindByClientIdAsync(string? clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 128)
            return Task.FromResult<OAuthClient?>(null);

        return db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == clientId && c.RevokedAt == null, ct);
    }

    public static bool VerifySecret(OAuthClient client, string? presentedSecret)
    {
        if (client.ClientSecretHash is null)
            return true;   // public client: nothing to verify

        return !string.IsNullOrEmpty(presentedSecret)
            && BCrypt.Net.BCrypt.Verify(presentedSecret, client.ClientSecretHash);
    }

    /// <summary>
    /// Validates and stores a dynamic client registration (RFC 7591).
    /// Returns the response body on success, or an error to return as 400.
    /// </summary>
    public async Task<(ClientRegistrationResponse? Response, OAuthError? Error)> RegisterDynamicAsync(
        ClientRegistrationRequest request,
        CancellationToken ct = default)
    {
        if (request.RedirectUris.Count == 0)
            return (null, new OAuthError(OAuthErrors.InvalidRedirectUri, "At least one redirect URI is required."));

        if (request.RedirectUris.Count > 10)
            return (null, new OAuthError(OAuthErrors.InvalidRedirectUri, "At most 10 redirect URIs are allowed."));

        foreach (var uri in request.RedirectUris)
        {
            var problem = OAuthRedirectUri.ValidateForRegistration(uri, _settings.AllowedRedirectHosts);
            if (problem is not null)
                return (null, new OAuthError(OAuthErrors.InvalidRedirectUri, problem));
        }

        var grantTypes = request.GrantTypes is { Count: > 0 }
            ? request.GrantTypes
            : [.. OAuthGrantTypes.Supported];

        if (grantTypes.Any(g => !OAuthGrantTypes.Supported.Contains(g, StringComparer.Ordinal)))
        {
            return (null, new OAuthError(OAuthErrors.InvalidClientMetadata,
                "Only the authorization_code and refresh_token grant types are supported."));
        }

        var responseTypes = request.ResponseTypes is { Count: > 0 } ? request.ResponseTypes : ["code"];
        if (responseTypes.Any(r => r != "code"))
            return (null, new OAuthError(OAuthErrors.InvalidClientMetadata, "Only the code response type is supported."));

        var authMethod = string.IsNullOrWhiteSpace(request.TokenEndpointAuthMethod)
            ? OAuthAuthMethods.None
            : request.TokenEndpointAuthMethod;

        if (!OAuthAuthMethods.Supported.Contains(authMethod, StringComparer.Ordinal))
            return (null, new OAuthError(OAuthErrors.InvalidClientMetadata, "Unsupported token_endpoint_auth_method."));

        var clientName = string.IsNullOrWhiteSpace(request.ClientName)
            ? "Unnamed client"
            : request.ClientName.Trim();

        if (clientName.Length > 200)
            clientName = clientName[..200];

        if (request.ClientUri is { Length: > 500 })
            return (null, new OAuthError(OAuthErrors.InvalidClientMetadata, "client_uri is too long."));

        var clientId = $"mcp_{Pkce.RandomToken(18)}";
        string? plainSecret = null;
        string? secretHash = null;

        if (OAuthAuthMethods.RequiresSecret(authMethod))
        {
            plainSecret = Pkce.RandomToken(32);
            secretHash = BCrypt.Net.BCrypt.HashPassword(plainSecret);
        }

        var client = new OAuthClient
        {
            ClientId = clientId,
            ClientSecretHash = secretHash,
            ClientName = clientName,
            RedirectUris = [.. request.RedirectUris],
            GrantTypes = [.. grantTypes],
            TokenEndpointAuthMethod = authMethod,
            ClientUri = request.ClientUri,
            IsStatic = false,
            CreatedAt = DateTime.UtcNow,
        };

        db.OAuthClients.Add(client);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("[MCP OAuth] Registered client {ClientId} ({ClientName})", clientId, clientName);

        return (new ClientRegistrationResponse
        {
            ClientId = clientId,
            ClientIdIssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ClientSecret = plainSecret,
            ClientSecretExpiresAt = plainSecret is null ? null : 0,
            ClientName = clientName,
            RedirectUris = [.. request.RedirectUris],
            GrantTypes = [.. grantTypes],
            ResponseTypes = [.. responseTypes],
            TokenEndpointAuthMethod = authMethod,
            Scope = McpScopes.Join(McpScopes.All),
        }, null);
    }

    /// <summary>
    /// Creates or updates the clients declared in configuration. Runs at startup so an
    /// operator can pre-register a connector when dynamic registration is not usable.
    /// </summary>
    public async Task EnsureStaticClientsAsync(CancellationToken ct = default)
    {
        foreach (var configured in _settings.StaticClients)
        {
            if (string.IsNullOrWhiteSpace(configured.ClientId) || configured.RedirectUris.Count == 0)
            {
                logger.LogWarning("[MCP OAuth] Skipping static client with missing ClientId or RedirectUris.");
                continue;
            }

            var invalid = configured.RedirectUris
                .Select(u => OAuthRedirectUri.ValidateForRegistration(u, _settings.AllowedRedirectHosts))
                .FirstOrDefault(p => p is not null);

            if (invalid is not null)
            {
                logger.LogWarning("[MCP OAuth] Skipping static client {ClientId}: {Problem}", configured.ClientId, invalid);
                continue;
            }

            var existing = await db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == configured.ClientId, ct);
            var hash = string.IsNullOrEmpty(configured.ClientSecret)
                ? null
                : BCrypt.Net.BCrypt.HashPassword(configured.ClientSecret);

            if (existing is null)
            {
                db.OAuthClients.Add(new OAuthClient
                {
                    ClientId = configured.ClientId,
                    ClientSecretHash = hash,
                    ClientName = string.IsNullOrWhiteSpace(configured.ClientName) ? configured.ClientId : configured.ClientName,
                    RedirectUris = [.. configured.RedirectUris],
                    GrantTypes = [.. OAuthGrantTypes.Supported],
                    TokenEndpointAuthMethod = hash is null ? OAuthAuthMethods.None : OAuthAuthMethods.ClientSecretPost,
                    IsStatic = true,
                    CreatedAt = DateTime.UtcNow,
                });
                logger.LogInformation("[MCP OAuth] Seeded static client {ClientId}", configured.ClientId);
            }
            else
            {
                existing.ClientName = string.IsNullOrWhiteSpace(configured.ClientName) ? existing.ClientName : configured.ClientName;
                existing.RedirectUris = [.. configured.RedirectUris];
                existing.TokenEndpointAuthMethod = hash is null ? OAuthAuthMethods.None : OAuthAuthMethods.ClientSecretPost;
                existing.IsStatic = true;
                existing.RevokedAt = null;
                if (hash is not null)
                    existing.ClientSecretHash = hash;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

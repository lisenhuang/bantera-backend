using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BanteraApi.Mcp;

/// <summary>
/// Issues and describes MCP access tokens. These are deliberately signed with a different
/// key and bound to a different audience than the mobile app's tokens (<see cref="Auth.JwtService"/>),
/// so neither token type is accepted by the other's endpoints.
/// </summary>
public class McpTokenService(IOptions<McpSettings> options)
{
    private readonly McpSettings _settings = options.Value;

    public int AccessTokenSeconds => _settings.AccessTokenMinutes * 60;

    public string IssueAccessToken(Guid userId, string role, IEnumerable<string> scopes, string clientId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, role),   // for ASP.NET authorization policies
            new Claim("role", role),            // mirrors JwtService for consistency
            new Claim(McpAuthDefaults.ScopeClaim, McpScopes.Join(scopes)),
            new Claim(McpAuthDefaults.ClientIdClaim, clientId),
        };

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.ResourceUrl,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_settings.AccessTokenMinutes),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Opaque refresh token; the caller stores only <see cref="Lookup"/> of it.</summary>
    public static string GenerateRefreshToken() => Pkce.RandomToken(32);

    /// <summary>Opaque authorization code.</summary>
    public static string GenerateAuthorizationCode() => Pkce.RandomToken(32);

    /// <summary>
    /// Deterministic SHA-256 hex fingerprint used as the DB lookup key for codes and
    /// refresh tokens. These are 256-bit random values, so a fast hash is appropriate —
    /// there is no low-entropy input to brute force.
    /// </summary>
    public static string Lookup(string plainToken)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainToken)));

    /// <summary>
    /// Validation parameters for the MCP bearer scheme. Shared by Program.cs and the tests
    /// so there is exactly one definition of what makes an MCP token valid.
    /// </summary>
    public static TokenValidationParameters BuildValidationParameters(string issuer, string resourceUrl, string signingKey)
        => new()
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = resourceUrl,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ClockSkew = TimeSpan.Zero,
        };
}

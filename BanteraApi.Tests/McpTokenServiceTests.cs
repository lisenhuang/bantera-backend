using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BanteraApi.Auth;
using BanteraApi.Mcp;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BanteraApi.Tests;

public class McpTokenServiceTests
{
    private const string McpKey = "mcp-test-signing-key-at-least-32-characters-long";
    private const string AppKey = "app-test-signing-key-at-least-32-characters-long";
    private const string Issuer = "https://api.example.test";
    private const string Resource = "https://api.example.test/mcp";

    private static McpTokenService CreateService() => new(Options.Create(new McpSettings
    {
        Issuer = Issuer,
        ResourceUrl = Resource,
        SigningKey = McpKey,
        AccessTokenMinutes = 60,
    }));

    private static JwtService CreateAppService() => new(Options.Create(new JwtSettings
    {
        Issuer = "bantera-api",
        Audience = "bantera-app",
        Secret = AppKey,
        AccessTokenExpiryMinutes = 60,
    }));

    [Fact]
    public void IssueAccessToken_EmitsExpectedClaims()
    {
        var userId = Guid.NewGuid();
        var token = CreateService().IssueAccessToken(userId, "admin", [McpScopes.Read, McpScopes.Write], "mcp_client");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(Issuer, jwt.Issuer);
        Assert.Contains(Resource, jwt.Audiences);
        Assert.Equal(userId.ToString(), jwt.Claims.First(c => c.Type == "sub").Value);
        Assert.Equal("admin", jwt.Claims.First(c => c.Type == "role").Value);
        Assert.Equal("mcp:read mcp:write", jwt.Claims.First(c => c.Type == "scope").Value);
        Assert.Equal("mcp_client", jwt.Claims.First(c => c.Type == "client_id").Value);
        Assert.NotEmpty(jwt.Claims.First(c => c.Type == "jti").Value);
    }

    [Fact]
    public void IssueAccessToken_EmitsRoleClaimUsableByAuthorizationPolicies()
    {
        var token = CreateService().IssueAccessToken(Guid.NewGuid(), "admin", [McpScopes.Read], "c");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        // Both the short "role" claim and the framework role claim type are emitted, so
        // RequireRole("admin") works while MapInboundClaims is disabled.
        Assert.Contains(jwt.Claims, c => c.Type == ClaimTypes.Role && c.Value == "admin");
        Assert.Contains(jwt.Claims, c => c.Type == "role" && c.Value == "admin");
    }

    [Fact]
    public void IssuedToken_ValidatesAgainstItsOwnParameters()
    {
        var token = CreateService().IssueAccessToken(Guid.NewGuid(), "admin", [McpScopes.Read], "c");

        var principal = new JwtSecurityTokenHandler().ValidateToken(
            token,
            McpTokenService.BuildValidationParameters(Issuer, Resource, McpKey),
            out _);

        Assert.True(McpScopes.Has(principal, McpScopes.Read));
    }

    [Fact]
    public void AppToken_IsRejectedByMcpValidation()
    {
        // The whole point of the separate scheme: a mobile app token must never be
        // accepted on the MCP endpoint.
        var appToken = CreateAppService().GenerateAccessToken(Guid.NewGuid(), "admin");

        Assert.ThrowsAny<SecurityTokenException>(() =>
            new JwtSecurityTokenHandler().ValidateToken(
                appToken,
                McpTokenService.BuildValidationParameters(Issuer, Resource, McpKey),
                out _));
    }

    [Fact]
    public void McpToken_IsRejectedByAppValidation()
    {
        var mcpToken = CreateService().IssueAccessToken(Guid.NewGuid(), "admin", [McpScopes.Read], "c");

        var appParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "bantera-api",
            ValidateAudience = true,
            ValidAudience = "bantera-app",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AppKey)),
            ClockSkew = TimeSpan.Zero,
        };

        Assert.ThrowsAny<SecurityTokenException>(() =>
            new JwtSecurityTokenHandler().ValidateToken(mcpToken, appParameters, out _));
    }

    [Fact]
    public void TokenSignedWithDifferentKey_IsRejected()
    {
        var token = CreateService().IssueAccessToken(Guid.NewGuid(), "admin", [McpScopes.Read], "c");

        Assert.ThrowsAny<SecurityTokenException>(() =>
            new JwtSecurityTokenHandler().ValidateToken(
                token,
                McpTokenService.BuildValidationParameters(Issuer, Resource, AppKey),
                out _));
    }

    [Fact]
    public void Lookup_IsDeterministicAndDiffersPerToken()
    {
        var a = McpTokenService.GenerateRefreshToken();
        var b = McpTokenService.GenerateRefreshToken();

        Assert.Equal(McpTokenService.Lookup(a), McpTokenService.Lookup(a));
        Assert.NotEqual(McpTokenService.Lookup(a), McpTokenService.Lookup(b));
        Assert.Equal(64, McpTokenService.Lookup(a).Length);   // SHA-256 hex
    }
}

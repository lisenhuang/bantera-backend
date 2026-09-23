using System.Security.Claims;
using ModelContextProtocol;

namespace BanteraApi.Mcp;

/// <summary>
/// The authenticated admin behind the current MCP tool call. Scoped, resolved from the
/// HTTP context that carries the validated MCP access token.
/// </summary>
public class McpToolContext(IHttpContextAccessor accessor)
{
    public HttpContext? HttpContext => accessor.HttpContext;

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    /// <summary>The admin's user id, from the token subject.</summary>
    public Guid AdminUserId
    {
        get
        {
            var id = TryGetUserId(Principal);
            return id ?? throw new McpException("Not authenticated.");
        }
    }

    public IReadOnlySet<string> Scopes => McpScopes.FromPrincipal(Principal);

    public string? ClientId => Principal?.FindFirst(McpAuthDefaults.ClientIdClaim)?.Value;

    public bool CanWrite => Scopes.Contains(McpScopes.Write);

    /// <summary>
    /// Guards a write tool. The MCP authorization policy already blocks these, but an
    /// explicit check means a wiring mistake cannot silently grant write access.
    /// </summary>
    public void RequireWrite()
    {
        if (!CanWrite)
            throw new McpException("This action requires the mcp:write permission, which was not granted for this connection.");
    }

    public static Guid? TryGetUserId(ClaimsPrincipal? principal)
    {
        var raw = principal?.FindFirst("sub")?.Value
            ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(raw, out var id) && id != Guid.Empty ? id : null;
    }
}

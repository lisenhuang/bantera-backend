namespace BanteraApi.Mcp;

/// <summary>
/// Mounts the MCP endpoint. Requires an MCP access token carrying at least mcp:read;
/// write tools are additionally gated by the McpWrite policy on the tool class itself.
/// </summary>
public static class McpEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapMcp("/mcp")
           .RequireAuthorization(McpAuthDefaults.ReadPolicy);
    }
}

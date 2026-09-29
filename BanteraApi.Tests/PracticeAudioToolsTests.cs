using System.Security.Claims;
using BanteraApi.Mcp;
using BanteraApi.Mcp.Tools;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace BanteraApi.Tests;

public class PracticeAudioToolsTests
{
    [Fact]
    public void PublishedMcpSchemaExposesLevelWithoutRequiringItForOldCallers()
    {
        var tool = McpServerTool.Create(
            typeof(PracticeAudioTools).GetMethod(nameof(PracticeAudioTools.SubmitAsync))!,
            CreateTool());
        var schema = tool.ProtocolTool.InputSchema;
        Assert.True(schema.GetProperty("properties").TryGetProperty("level", out var level));
        Assert.Contains("advanced", level.GetProperty("description").GetString());
        Assert.DoesNotContain(schema.GetProperty("required").EnumerateArray(),
            property => property.GetString() == "level");
    }

    [Theory]
    [InlineData("all")]
    [InlineData("native")]
    [InlineData("")]
    public async Task InvalidLevelIsRejectedBeforeDatabaseOrStorageAccess(string level)
    {
        // Dependencies are deliberately absent: validation must run before any side effects.
        var error = await Assert.ThrowsAsync<McpException>(() => CreateTool().SubmitAsync(
            Guid.NewGuid(), "mp3", "News", "English", "en-US", "Hello.", 1000,
            ["Hello."], [new(0, 0, 1000, "Hello.")], [new("Hello", 0, 900, null)],
            level: level));
        Assert.StartsWith("level must be", error.Message);
    }

    private static PracticeAudioTools CreateTool()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", Guid.NewGuid().ToString()),
                new Claim(McpAuthDefaults.ScopeClaim, McpScopes.Write),
            ], "test")),
        };
        return new PracticeAudioTools(null!,
            new McpToolContext(new HttpContextAccessor { HttpContext = context }),
            null!, null!, null!, null!);
    }
}

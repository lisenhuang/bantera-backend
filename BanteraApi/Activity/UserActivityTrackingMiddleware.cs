using BanteraApi.Database;

namespace BanteraApi.Activity;

/// <summary>
/// Records daily activity for authenticated app requests.
///
/// Runs after the response has been produced and never throws, so it cannot fail or slow
/// a request. Admin console traffic (/api/admin), MCP traffic (/mcp) and infrastructure
/// endpoints are excluded so that operating the product does not inflate usage metrics.
/// </summary>
public class UserActivityTrackingMiddleware(
    RequestDelegate next,
    UserActivityRecorder recorder,
    ILogger<UserActivityTrackingMiddleware> logger)
{
    private static readonly string[] ExcludedPrefixes =
    [
        "/mcp",
        "/api/admin",
        "/oauth",
        "/.well-known",
        "/swagger",
        "/version",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        finally
        {
            if (TryGetTrackableUserId(context, out var userId))
            {
                try
                {
                    var db = context.RequestServices.GetRequiredService<AppDbContext>();
                    // Not context.RequestAborted: a client disconnect must not cancel the write.
                    await recorder.TouchAsync(userId, db, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Activity] Tracking failed for user {UserId}", userId);
                }
            }
        }
    }

    private static bool TryGetTrackableUserId(HttpContext context, out Guid userId)
    {
        userId = Guid.Empty;

        if (context.User.Identity?.IsAuthenticated != true)
            return false;

        var path = context.Request.Path;
        foreach (var prefix in ExcludedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // The Bantera AI placeholder account is not a real user.
        if (context.User.FindFirst("role")?.Value == "system")
            return false;

        var raw = context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(raw, out userId) && userId != Guid.Empty;
    }
}

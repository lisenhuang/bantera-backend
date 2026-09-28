using System.Security.Claims;
using BanteraApi.Auth;
using BanteraApi.Chat;
using Microsoft.Extensions.Options;

namespace BanteraApi.Admin;

public static class ChatCallSettingsEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/call-settings").RequireAuthorization("Admin");
        group.MapGet("", async (HttpContext context, ChatCallSettingsService settings,
            IOptions<CloudflareTurnSettings> turn, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(await GetResponseAsync(settings, turn.Value, ct));
        }).WithName("AdminGetCallSettings");

        group.MapPut("", async (UpdateCallSettingsRequest request, ClaimsPrincipal user,
            ChatCallSettingsService settings, IOptions<CloudflareTurnSettings> turn, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirst("sub")?.Value, out var adminId))
                return Results.Json(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."), statusCode: 401);
            if (!ChatCallSettingsService.IsValidPolicy(request.IceTransportPolicy))
                return Results.BadRequest(new ApiError("invalid_call_mode", "Choose a valid call connection mode."));
            if (request.IceTransportPolicy == "relay" && !turn.Value.IsConfigured)
                return Results.BadRequest(new ApiError("relay_unavailable", "Configure the call relay before enabling TURN only."));

            await settings.SetAsync(request.IceTransportPolicy!, adminId, ct);
            return Results.Ok(await GetResponseAsync(settings, turn.Value, ct));
        }).WithName("AdminUpdateCallSettings");
    }

    private static async Task<object> GetResponseAsync(ChatCallSettingsService settings,
        CloudflareTurnSettings turn, CancellationToken ct)
    {
        var row = await settings.GetRowAsync(ct);
        return new
        {
            iceTransportPolicy = ChatCallSettingsService.ResolvePolicy(row?.Value),
            turnConfigured = turn.IsConfigured,
            updatedAt = row?.UpdatedAt,
        };
    }

    public sealed record UpdateCallSettingsRequest(string? IceTransportPolicy);
}

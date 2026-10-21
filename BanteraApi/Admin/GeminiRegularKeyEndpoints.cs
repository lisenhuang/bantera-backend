using System.Security.Claims;
using BanteraApi.Auth;
using BanteraApi.Gemini;

namespace BanteraApi.Admin;

public static class GeminiRegularKeyEndpoints
{
    public sealed record AddRequest(string? ApiKey, string? Remarks);
    public sealed record UpdateRequest(string? Remarks, bool Enabled);
    internal static bool ValidKey(string? key) => key is { Length: >= 20 and <= 4096 } &&
        key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '=' or '+' or '/');
    internal static bool ValidRemarks(string? remarks) => (remarks?.Length ?? 0) <= 1000;
    private static bool ValidId(string id) => id.Length == 64 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/ai-settings/regular-keys").RequireAuthorization("Admin");
        group.AddEndpointFilter(async (context, next) => {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("", async (GeminiRegularKeyStore store, CancellationToken ct) => Results.Ok(await store.StatusAsync(ct)));
        group.MapPost("", async (AddRequest req, ClaimsPrincipal principal, GeminiRegularKeyStore store, CancellationToken ct) => {
            var key = req.ApiKey?.Trim();
            if (!ValidKey(key)) return Results.BadRequest(new ApiError("invalid_key_format", "Paste a complete Gemini API key without quotes or spaces."));
            if (!ValidRemarks(req.Remarks)) return Results.BadRequest(new ApiError("invalid_remarks", "Remarks must be 1,000 characters or fewer."));
            return await Save(principal, store, admin => store.AddAsync(key!, req.Remarks?.Trim() ?? "", admin, ct));
        });
        group.MapPut("/{id}", async (string id, UpdateRequest req, ClaimsPrincipal principal, GeminiRegularKeyStore store, CancellationToken ct) => {
            if (!ValidId(id)) return Results.BadRequest(new ApiError("invalid_id", "Invalid key identifier."));
            if (!ValidRemarks(req.Remarks)) return Results.BadRequest(new ApiError("invalid_remarks", "Remarks must be 1,000 characters or fewer."));
            return await Save(principal, store, admin => store.UpdateAsync(id, req.Remarks?.Trim() ?? "", req.Enabled, admin, ct));
        });
        group.MapDelete("/{id}", async (string id, ClaimsPrincipal principal, GeminiRegularKeyStore store, CancellationToken ct) => {
            if (!ValidId(id)) return Results.BadRequest(new ApiError("invalid_id", "Invalid key identifier."));
            return await Save(principal, store, admin => store.RemoveAsync(id, admin, ct));
        });
    }

    private static async Task<IResult> Save(ClaimsPrincipal principal, GeminiRegularKeyStore store, Func<Guid, Task> save)
    {
        if (!Guid.TryParse(principal.FindFirst("sub")?.Value, out var admin)) return Results.Unauthorized();
        if (!store.StorageReady) return Results.Json(new ApiError("storage_unavailable", "Secure key storage is unavailable."), statusCode: 503);
        try { await save(admin); }
        catch (ArgumentException ex) when (ex.Message is "duplicate_key" or "pool_full" or "key_not_found" or "environment_key" or "paid_key") {
            return Results.Json(new ApiError(ex.Message, "Could not update this key."), statusCode: ex.Message == "key_not_found" ? 404 : 409);
        }
        return Results.NoContent();
    }
}

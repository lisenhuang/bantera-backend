using System.Security.Claims;
using BanteraApi.Auth;
using BanteraApi.Database;
using BanteraApi.Gemini;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Admin;

public static class GeminiPaidFallbackEndpoints
{
    public sealed record UpdateRequest(string? ApiKey, bool RemoveKey, Guid[]? AllowedUserIds, Guid[]? PaidOnlyUserIds = null);
    internal static ApiError? Validate(UpdateRequest req)
    {
        var key = req.ApiKey?.Trim();
        if (req.AllowedUserIds is null || req.AllowedUserIds.Length > 1000 || (req.PaidOnlyUserIds?.Length ?? 0) > 1000 ||
            (req.PaidOnlyUserIds ?? []).Except(req.AllowedUserIds).Any())
            return new("invalid_users", "Choose the users allowed to use the paid key.");
        // Auth keys can contain periods and be longer than legacy AIza keys.
        if (key is { Length: > 0 } && (key.Length is < 20 or > 4096 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '=' or '+' or '/')))
            return new("invalid_key_format", "Paste the complete Gemini API key without quotes or spaces. Both AIza and AQ. keys are accepted.");
        if (req.RemoveKey && !string.IsNullOrEmpty(key))
            return new("conflicting_key_changes", "Choose either replacing the key or removing it.");
        return null;
    }
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/ai-settings/paid-fallback").RequireAuthorization("Admin");
        group.MapGet("", async (GeminiPaidFallback store, CancellationToken ct) => Results.Ok(await store.StatusAsync(ct)));
        group.MapGet("/users", async (string? q, AppDbContext db, CancellationToken ct) => {
            var query = q?.Trim();
            if (query is null || query.Length < 2 || query.Length > 100) return Results.Ok(Array.Empty<PaidFallbackUser>());
            var term = query.ToLowerInvariant();
            var users = await db.Users.AsNoTracking().Where(u => u.DeletedAt == null && u.Status == "active" &&
                ((u.Name != null && u.Name.ToLower().Contains(term)) || u.Identities.Any(i => i.ProviderEmail != null && i.ProviderEmail.ToLower().Contains(term))))
                .OrderBy(u => u.Name).Take(20)
                .Select(u => new PaidFallbackUser(u.Id, u.Name, u.Identities.Select(i => i.ProviderEmail).FirstOrDefault(), "fallback")).ToArrayAsync(ct);
            return Results.Ok(users);
        });
        group.MapPut("", async (UpdateRequest req, ClaimsPrincipal principal, GeminiPaidFallback store, CancellationToken ct) => {
            if (!Guid.TryParse(principal.FindFirst("sub")?.Value, out var admin)) return Results.Unauthorized();
            if (!store.StorageReady) return Results.Json(new ApiError("storage_unavailable", "Secure key storage is unavailable."), statusCode: 503);
            var key = req.ApiKey?.Trim();
            if (Validate(req) is { } error) return Results.BadRequest(error);
            try { await store.SaveAsync(key, req.RemoveKey, req.AllowedUserIds!, req.PaidOnlyUserIds ?? [], admin, ct); }
            catch (ArgumentException) { return Results.BadRequest(new ApiError("invalid_user", "Choose active users only.")); }
            return Results.Ok(await store.StatusAsync(ct));
        });
    }
}

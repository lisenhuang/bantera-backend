using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace BanteraApi.OpenAi;

public static class ChatGptConnectionEndpoints
{
    public sealed record StartRequest(string BrowserBinding);
    public sealed record CompleteRequest(string BrowserBinding, string State, string? Code, string? Error);
    public sealed record PollRequest(string BrowserBinding, string Attempt);
    public sealed record TestRequest(string Model, string? Reasoning, string Prompt, bool Search = false);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/ai-settings/chatgpt").RequireAuthorization("Admin")
            .RequireRateLimiting("admin-chatgpt");
        group.AddEndpointFilter(async (context, next) => {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (Exception ex) when (ex is ChatGptConnectionException or HttpRequestException or SecurityTokenException or CryptographicException or JsonException or IOException or TaskCanceledException) {
                var code = ex is ChatGptConnectionException known ? known.Code : "connection_failed";
                // Never log OAuth responses, callback codes, authorization URLs or tokens.
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ChatGptConnection")
                    .LogWarning("ChatGPT connection operation failed: {Code}, {ExceptionType}, provider={ProviderCode}, stage={Stage}", code, ex.GetType().Name, (ex as ChatGptConnectionException)?.ProviderCode, (ex as ChatGptConnectionException)?.Stage);
                return Results.Json(new { code, message = ErrorMessage(code) }, statusCode: code is "setup_required" or "storage_required" ? 409 : 400);
            }
        });
        group.MapGet("", (ChatGptConnection service, CancellationToken ct) => service.StatusAsync(ct));
        group.MapPost("/device/start", async (StartRequest body, ClaimsPrincipal user, ChatGptConnection service, CancellationToken ct) =>
            Results.Ok(await service.StartDeviceAsync(Admin(user), body.BrowserBinding ?? "", ct)));
        group.MapPost("/device/poll", async (PollRequest body, ClaimsPrincipal user, ChatGptConnection service, CancellationToken ct) =>
            Results.Ok(new { status = await service.PollDeviceAsync(Admin(user), body.BrowserBinding ?? "", body.Attempt ?? "", ct) }));
        group.MapPost("/device/cancel", async (PollRequest body, ClaimsPrincipal user, ChatGptConnection service, CancellationToken ct) => {
            await service.CancelDeviceAsync(Admin(user), body.BrowserBinding ?? "", body.Attempt ?? "", ct);
            return Results.Ok(new { cancelled = true });
        });
        group.MapGet("/models", (ChatGptSubscriptionClient service, CancellationToken ct) => service.ModelsAsync(ct));
        group.MapPost("/test", async (TestRequest body, ChatGptSubscriptionClient service, CancellationToken ct) => {
            if (body.Prompt?.Trim().Length is not (>= 1 and <= 2000) || body.Model?.Length is not (>= 1 and <= 128))
                return Results.BadRequest(new { code = "invalid_request", message = "Choose a model and enter a prompt of up to 2,000 characters." });
            try { return Results.Ok(await service.TestAsync(body.Model, body.Reasoning, body.Prompt.Trim(), body.Search, ct)); }
            catch (TimeoutException) { return Results.Json(new { code = "response_timeout", message = "The GPT response reached your configured timeout. Try a lower reasoning level or increase the timeout in model settings." }, statusCode: 504); }
        }).RequireRateLimiting("admin-search-test");
        group.MapPost("/start", async (StartRequest body, ClaimsPrincipal user, ChatGptConnection service, CancellationToken ct) =>
            Results.Ok(new { authorizationUrl = await service.StartAsync(Admin(user), body.BrowserBinding ?? "", ct) }));
        group.MapPost("/complete", async (CompleteRequest body, ClaimsPrincipal user, ChatGptConnection service, CancellationToken ct) => {
            await service.CompleteAsync(Admin(user), body.BrowserBinding ?? "", body.State ?? "", body.Code, body.Error, ct);
            return Results.Ok(new { connected = true });
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16384));
        group.MapPost("/disconnect", async (ClaimsPrincipal user, ChatGptConnection service, CancellationToken ct) =>
            Results.Ok(new { disconnected = true, revocationConfirmed = await service.DisconnectAsync(Admin(user), ct) }));
    }
    private static string ErrorMessage(string code) => code switch {
        "device_auth_disabled" => "Enable device code authentication for Codex in your ChatGPT security settings, then try again.",
        "storage_required" => "Secure credential storage is not configured on the backend.",
        "setup_required" => "Hosted OAuth is not configured. Use device-code sign-in instead.",
        "reconnect_required" => "Reconnect your ChatGPT account and try again.",
        "quota_exceeded" => "Your ChatGPT usage limit was reached. Try again after it resets.",
        "catalog_unavailable" => "The current model list could not be loaded. Please retry.",
        "unsupported_request" => "This model or subscription connection does not support the requested operation. Try another model or turn off web search.",
        "invalid_model" or "invalid_reasoning" => "Reload the model list and choose a supported model and reasoning level.",
        "response_incomplete" => "The response ended before completion. Please retry.",
        _ => "ChatGPT could not complete this request. Please try again."
    };
    private static Guid Admin(ClaimsPrincipal user) => Guid.TryParse(user.FindFirstValue("sub"), out var id)
        ? id : throw new ChatGptConnectionException("invalid_admin");
}

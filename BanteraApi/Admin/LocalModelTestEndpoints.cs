using System.Net;
using System.Net.Http.Headers;
using BanteraApi.Database;
using BanteraApi.Gemini;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;

namespace BanteraApi.Admin;

/// <summary>Additive admin-only gateway. The private worker owns durable jobs and enforces ownership.</summary>
public static class LocalModelTestEndpoints
{
    public const int MaxBodyBytes = 12 * 1024 * 1024;
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/model-tests").RequireAuthorization("Admin");
        // Check current account state too: an old admin token must not outlive a role revocation.
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "private, no-store";
            if (!Guid.TryParse(http.User.FindFirst("sub")?.Value, out var id)) return Results.Unauthorized();
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            if (!await db.Users.AnyAsync(u => u.Id == id && u.Role == "admin" && u.Status == "active" && u.DeletedAt == null, http.RequestAborted))
                return Results.Forbid();
            return await next(context);
        });
        group.MapGet("/generation-settings", async (AiModelSettingsService settings, CancellationToken ct) => {
            var m = await settings.GetAsync(ct);
            return Results.Ok(new { m.TextModel, m.AudioModel, m.FallbackTextModel, m.FallbackAudioModel,
                minWordCount = 5, maxWordCount = 60, voice = "App Practice voice", generationTimeoutSeconds = 600 });
        });
        group.MapGet("/jobs/{id:guid}/audio", (Guid id, HttpContext c, IHttpClientFactory f, IConfiguration s) => Forward(c, f, s, $"jobs/{id}/audio"));
        group.MapGet("/status", (HttpContext c, IHttpClientFactory f, IConfiguration s) => Forward(c, f, s, "status"));
        group.MapGet("/jobs", (HttpContext c, IHttpClientFactory f, IConfiguration s) => Forward(c, f, s, "jobs"));
        group.MapPost("/jobs", (HttpContext c, IHttpClientFactory f, IConfiguration s) => Forward(c, f, s, "jobs"));
        group.MapGet("/jobs/{id:guid}", (Guid id, HttpContext c, IHttpClientFactory f, IConfiguration s) => Forward(c, f, s, $"jobs/{id}"));
        group.MapDelete("/jobs/{id:guid}", (Guid id, HttpContext c, IHttpClientFactory f, IConfiguration s) => Forward(c, f, s, $"jobs/{id}"));
        group.MapPut("/jobs/{id:guid}/assessment", (Guid id, HttpContext c, IHttpClientFactory f, IConfiguration s) => Forward(c, f, s, $"jobs/{id}/assessment"));
    }

    internal static async Task<IResult> Forward(HttpContext context, IHttpClientFactory clients, IConfiguration config, string path)
    {
        var origin = config["LocalModelTests:BaseUrl"];
        var key = config["LocalModelTests:ServiceKey"];
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(key))
            return Results.Json(new { error = "Local model testing is not configured." }, statusCode: 503);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), $"{origin.TrimEnd('/')}/{path}");
        request.Headers.Add("X-Service-Key", key);
        request.Headers.Add("X-Owner-Id", context.User.FindFirst("sub")!.Value);
        if (context.Request.Method is "POST" or "PUT")
        {
            var limit = context.Request.Method == "PUT" ? 16 * 1024 : MaxBodyBytes;
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
            if (context.Request.ContentLength > limit) return Results.StatusCode(413);
            if (context.Request.ContentType?.Split(';')[0] != "application/json") return Results.StatusCode(415);
            using var body = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (body.Length + count > limit) return Results.StatusCode(413);
                await body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
            }
            request.Content = new ByteArrayContent(body.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        try
        {
            using var response = await clients.CreateClient("local-model-tests").SendAsync(request, context.RequestAborted);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.Unauthorized)
                return Results.Json(new { error = "The local model service is unavailable. Please retry shortly." }, statusCode: 503);
            if (path.EndsWith("/audio", StringComparison.Ordinal) && response.IsSuccessStatusCode)
                return Results.File(await response.Content.ReadAsByteArrayAsync(context.RequestAborted),
                    response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg", enableRangeProcessing: true);
            var body = await response.Content.ReadAsStringAsync(context.RequestAborted);
            return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Results.Json(new { error = "The local model service did not respond. Check job history before retrying." }, statusCode: 503);
        }
    }
}

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using BanteraApi.Database;
using BanteraApi.Gemini;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Jobs;

public sealed class OperationService(AppDbContext db, OperationCipher cipher, OperationRegistry registry)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string Failure = "This operation could not finish. Please try again.";
    public const string Interrupted = "The server stopped before completion could be confirmed. This request was not automatically repeated.";

    public async Task AcceptAsync(HttpContext context, string kind)
    {
        var ct = context.RequestAborted;
        if (!Guid.TryParse(context.User.FindFirstValue("sub"), out var owner)) { context.Response.StatusCode = 401; return; }
        var handler = registry.Get(kind);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == owner, ct);
        if (user is null || user.DeletedAt != null || user.Status != "active" || (handler.Admin && user.Role != "admin")) {
            context.Response.StatusCode = 403; return;
        }
        string body;
        try {
            // Explicit bound also covers chunked requests and wrappers around original endpoint metadata.
            using var buffer = new MemoryStream();
            var bytes = new byte[8192]; int read;
            while ((read = await context.Request.Body.ReadAsync(bytes, ct)) > 0) {
                if (buffer.Length + read > 200000) { context.Response.StatusCode = 413; return; }
                buffer.Write(bytes, 0, read);
            }
            body = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        } catch (JsonException) { context.Response.StatusCode = 400; return; }
        if (kind == "summary" && !BanteraApi.Chat.Ai.AiConversationSummary.Valid(JsonSerializer.Deserialize<BanteraApi.Chat.Ai.AiConversationSummary.Request>(body, Json)!)) {
            context.Response.StatusCode = 400; return;
        }
        if (kind == "transcript" && (!Guid.TryParse(context.Request.RouteValues["videoId"]?.ToString(), out var videoId) ||
            !await db.UserVideos.AnyAsync(x => x.Id == videoId && x.UserId == owner, ct))) {
            context.Response.StatusCode = 404; return;
        }
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length > 100) { context.Response.StatusCode = 400; return; }
        var node = JsonNode.Parse(body)!.AsObject();
        Guid id;
        if (kind.StartsWith("audio-") && Guid.TryParse(node["clientJobId"]?.ToString(), out var audioId) && audioId != Guid.Empty) id = audioId;
        else id = Guid.TryParse(key, out var explicitId) && explicitId != Guid.Empty ? explicitId : key.Length > 0 ? cipher.RequestId(owner, kind, key) : Guid.NewGuid();
        if (kind.StartsWith("audio-")) node["clientJobId"] = id.ToString();
        body = node.ToJsonString();
        var input = new OperationInput(body, context.Request.RouteValues.ToDictionary(x => x.Key, x => x.Value?.ToString()));
        var serialized = JsonSerializer.Serialize(input, Json);
        var hash = cipher.Hash(serialized);
        // Serialise account admission, including duplicate POSTs and pending-job limits across replicas.
        await using (var tx = await db.Database.BeginTransactionAsync(ct)) {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({owner.ToString()}))", ct);
            var existing = await db.AiOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (existing is not null) {
                if (existing.UserId != owner || existing.Kind != kind || existing.RequestHash != hash) {
                    context.Response.StatusCode = 409;
                    await context.Response.WriteAsJsonAsync(new { message = "This request ID is already in use." }, ct); return;
                }
            } else {
                if (kind.StartsWith("audio-")) {
                    var request = JsonSerializer.Deserialize<GenerateAudioRequest>(body, Json);
                    if (request is null || string.IsNullOrWhiteSpace(request.Language) || request.Scenario is null ||
                        !BanteraApi.LearningLanguageCatalog.SupportsAudioDialogues(request.LanguageCode)) {
                        context.Response.StatusCode = 422; return;
                    }
                    if (request.Level is not null && !BanteraApi.Gemini.AudioLevels.IsValid(request.Level)) {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { code = "invalid_level", message = "Select Beginner, Intermediate, or Advanced." }, ct); return;
                    }
                    var today = DateTime.UtcNow.Date;
                    var completed = await db.UserVideos.CountAsync(x => x.UserId == owner && x.IsAiGenerated && x.CreatedAt >= today, ct);
                    var pending = await db.AiOperations.CountAsync(x => x.UserId == owner && x.Kind.StartsWith("audio-") && (x.Status == "queued" || x.Status == "running"), ct);
                    if (completed + pending >= (user.AiAudioDailyLimit ?? 5)) {
                        context.Response.StatusCode = 429;
                        await context.Response.WriteAsJsonAsync(new { code = BanteraApi.Auth.ErrorCodes.DailyLimitReached, message = "Your audio generation limit has been reached. Please wait for existing jobs or try again tomorrow." }, ct); return;
                    }
                }
                var count = await db.AiOperations.CountAsync(x => x.UserId == owner && (x.Status == "queued" || x.Status == "running"), ct);
                if (count >= 5) { context.Response.StatusCode = 429; return; }
                db.AiOperations.Add(new AiOperation { Id = id, UserId = owner, Kind = kind, RequestHash = hash,
                    Input = cipher.Seal(serialized, id), ExpiresAt = DateTime.UtcNow.AddHours(kind == "summary" ? 1 : 24) });
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Location = $"/api/me/ai-jobs/{id}";
        if (context.Request.Headers["Prefer"].ToString().Contains("respond-async", StringComparison.OrdinalIgnoreCase)) {
            context.Response.StatusCode = 202;
            await context.Response.WriteAsJsonAsync(new { jobId = id, status = "queued", statusUrl = $"/api/me/ai-jobs/{id}" }, ct);
        } else if (handler.Streaming) {
            await StreamAsync(context, owner, id);
        } else {
            // Old published clients retain JSON responses; leave headroom for the proxy to return errors.
            var deadline = DateTime.UtcNow.AddSeconds(105);
            while (DateTime.UtcNow < deadline) {
                var job = await FindAsync(owner, id, ct);
                if (job?.Terminal == true) { await WriteResult(context, job); return; }
                await Task.Delay(1000, ct);
            }
            context.Response.StatusCode = 504;
            await context.Response.WriteAsJsonAsync(new { code = "generation_pending", message = "Still processing. Please check again shortly.", jobId = id }, ct);
        }
    }

    public async Task QueueInternalAsync(Guid owner, string kind, string key, object body, CancellationToken ct)
    {
        var id = cipher.RequestId(owner, kind, key);
        var input = JsonSerializer.Serialize(new OperationInput(JsonSerializer.Serialize(body, Json), new()), Json);
        var encrypted = cipher.Seal(input, id);
        var hash = cipher.Hash(input);
        var now = DateTime.UtcNow;
        var expires = now.AddDays(1);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ai_operations ("Id", "UserId", "Kind", "RequestHash", "Status", "Input", "StatusCode", "CreatedAt", "ExpiresAt")
            VALUES ({id}, {owner}, {kind}, {hash}, 'queued', {encrypted}, 200, {now}, {expires})
            ON CONFLICT ("Id") DO NOTHING
            """, ct);
    }

    public Task<AiOperation?> FindAsync(Guid owner, Guid id, CancellationToken ct) => db.AiOperations.AsNoTracking()
        .SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner && x.ExpiresAt > DateTime.UtcNow && db.Users.Any(u => u.Id == owner && u.Status == "active" && u.DeletedAt == null && (!x.Kind.StartsWith("admin-") || u.Role == "admin")), ct);

    public object Present(AiOperation job) => new {
        jobId = job.Id, kind = job.Kind, status = job.Status, job.CreatedAt, job.CompletedAt,
        events = Events(job), statusCode = job.StatusCode,
        result = job.Output is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(cipher.Open(job.Output, job.Id))
    };
    public List<OperationEvent> Events(AiOperation job) => job.Events is null ? [] :
        JsonSerializer.Deserialize<List<OperationEvent>>(cipher.Open(job.Events, job.Id), Json) ?? [];

    public async Task StreamAsync(HttpContext context, Guid owner, Guid id)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await using var stream = new GenerationProgressStream(context.Response, context.RequestAborted);
        await stream.SendAsync(new { step = "started", jobId = id });
        var sequence = 0;
        while (!context.RequestAborted.IsCancellationRequested) {
            var job = await FindAsync(owner, id, context.RequestAborted);
            if (job is null) { await stream.SendAsync(new { step = "error", message = Failure }); return; }
            var events = Events(job);
            foreach (var item in events.Where(x => x.Sequence > sequence)) {
                await stream.SendAsync(item.Data); sequence = item.Sequence;
            }
            if (job.Terminal) {
                if (!events.Any(x => x.Data.TryGetProperty("step", out var step) && step.GetString() is "done" or "error"))
                    await stream.SendAsync(job.Status == "done" ? new { step = "done", result = Present(job) } : (object)new { step = "error", message = Failure });
                return;
            }
            await Task.Delay(1000, context.RequestAborted);
        }
    }
    private async Task WriteResult(HttpContext context, AiOperation job)
    {
        context.Response.StatusCode = job.StatusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(job.Output is null ? JsonSerializer.Serialize(new { message = Failure }) : cipher.Open(job.Output, job.Id), context.RequestAborted);
    }
    public static void Map(WebApplication app)
    {
        var registry = app.Services.GetRequiredService<OperationRegistry>();
        registry.Add("cover", new(async ctx => {
            using var input = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            await ctx.RequestServices.GetRequiredService<BanteraApi.Videos.VideoService>().GenerateMissingCoverAsync(
                input.RootElement.GetProperty("videoId").GetGuid(), Guid.Parse(ctx.User.FindFirstValue("sub")!), ctx.RequestAborted);
            await ctx.Response.WriteAsJsonAsync(new { completed = true }, ctx.RequestAborted);
        }, false, false, TimeSpan.FromMinutes(3)));
        registry.Add("avatar", new(async ctx => {
            using var input = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var generated = await ctx.RequestServices.GetRequiredService<BanteraApi.Profile.ProfileService>().GenerateMissingAvatarAsync(
                Guid.Parse(ctx.User.FindFirstValue("sub")!), input.RootElement.GetProperty("avatarGender").GetString()!, ctx.RequestAborted);
            await ctx.Response.WriteAsJsonAsync(new { generated }, ctx.RequestAborted);
        }, false, false, TimeSpan.FromMinutes(3)));
        var group = app.MapGroup("/api/me/ai-jobs").RequireAuthorization();
        group.MapGet("", async (HttpContext ctx, AppDbContext db, OperationService service) => {
            if (!Guid.TryParse(ctx.User.FindFirstValue("sub"), out var owner)) return Results.Unauthorized();
            ctx.Response.Headers.CacheControl = "no-store";
            var jobs = await db.AiOperations.AsNoTracking().Where(x => x.UserId == owner && x.ExpiresAt > DateTime.UtcNow && db.Users.Any(u => u.Id == owner && u.Status == "active" && u.DeletedAt == null && (!x.Kind.StartsWith("admin-") || u.Role == "admin")))
                .OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync(ctx.RequestAborted);
            return Results.Ok(jobs.Select(service.Present));
        });
        group.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, OperationService service) => {
            if (!Guid.TryParse(ctx.User.FindFirstValue("sub"), out var owner)) return Results.Unauthorized();
            ctx.Response.Headers.CacheControl = "no-store";
            var job = await service.FindAsync(owner, id, ctx.RequestAborted);
            return job is null ? Results.NotFound() : Results.Ok(service.Present(job));
        });
        group.MapGet("/{id:guid}/events", async (Guid id, HttpContext ctx, OperationService service) => {
            if (!Guid.TryParse(ctx.User.FindFirstValue("sub"), out var owner)) { ctx.Response.StatusCode = 401; return; }
            if (await service.FindAsync(owner, id, ctx.RequestAborted) is null) { ctx.Response.StatusCode = 404; return; }
            await service.StreamAsync(ctx, owner, id);
        });
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) => {
            if (!Guid.TryParse(ctx.User.FindFirstValue("sub"), out var owner)) return Results.Unauthorized();
            await db.AiOperations.Where(x => x.Id == id && x.UserId == owner).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "cancelled").SetProperty(x => x.Input, (string?)null)
                .SetProperty(x => x.Output, (string?)null).SetProperty(x => x.Events, (string?)null)
                .SetProperty(x => x.StatusCode, 409).SetProperty(x => x.CompletedAt, DateTime.UtcNow), ctx.RequestAborted);
            return Results.NoContent();
        });
    }
}

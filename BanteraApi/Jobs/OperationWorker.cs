using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BanteraApi.Database;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Jobs;

public sealed class OperationWorker(IServiceScopeFactory scopes, ILogger<OperationWorker> logs) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        RunAsync(stoppingToken), RunAsync(stoppingToken));
    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested) {
            try { if (await RunOneAsync(stop)) continue; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { logs.LogWarning("AI work queue unavailable: {Type}", ex.GetType().Name); }
            try { await Task.Delay(2000, stop); } catch (OperationCanceledException) { return; }
        }
    }
    internal async Task<bool> RunOneAsync(CancellationToken stop)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var cipher = services.GetRequiredService<OperationCipher>();
        var now = DateTime.UtcNow;
        // A stale running provider call has an unknown outcome. Never replay it automatically.
        var stale = await db.AiOperations.AsNoTracking().Where(x => x.Status == "running" && x.LeaseUntil < now).Take(50).ToListAsync(stop);
        foreach (var expired in stale) {
            string? completed = null;
            try {
                var events = expired.Events is null ? [] : JsonSerializer.Deserialize<List<OperationEvent>>(cipher.Open(expired.Events, expired.Id), OperationService.Json)!;
                completed = events.LastOrDefault(x => x.Data.TryGetProperty("step", out var step) && step.GetString() == "done")?.Data.GetRawText();
            } catch { /* A rotated encryption key cannot recover an old result. */ }
            var status = completed is null ? "failed" : "done";
            var code = completed is null ? 503 : 200;
            var output = cipher.Seal(completed ?? JsonSerializer.Serialize(new { message = OperationService.Interrupted }), expired.Id);
            await db.AiOperations.Where(x => x.Id == expired.Id && x.Status == "running" && x.LeaseUntil < now).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status).SetProperty(x => x.StatusCode, code).SetProperty(x => x.Output, output)
                .SetProperty(x => x.Input, (string?)null).SetProperty(x => x.CompletedAt, now), stop);
        }
        await db.AiOperations.Where(x => x.ExpiresAt < now).ExecuteDeleteAsync(stop);
        var id = await db.AiOperations.AsNoTracking().Where(x => x.Status == "queued")
            .OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(stop);
        if (id is null) return false;
        var claimed = await db.AiOperations.Where(x => x.Id == id && x.Status == "queued").ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, "running").SetProperty(x => x.LeaseUntil, now.AddSeconds(60)), stop);
        if (claimed == 0) return true;
        var job = await db.AiOperations.AsNoTracking().SingleAsync(x => x.Id == id, stop);
        var registry = services.GetRequiredService<OperationRegistry>();
        var handler = registry.Get(job.Kind);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lifetime.CancelAfter(handler.Budget);
        var heartbeat = MaintainLease(job, handler, lifetime);
        var context = new DefaultHttpContext { RequestServices = services, RequestAborted = lifetime.Token };
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        try {
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.UserId, lifetime.Token);
            if (user is null || user.Status != "active" || user.DeletedAt != null || (handler.Admin && user.Role != "admin"))
                throw new UnauthorizedAccessException();
            var input = JsonSerializer.Deserialize<OperationInput>(cipher.Open(job.Input!, job.Id), OperationService.Json)!;
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("sub", job.UserId.ToString()), new Claim(ClaimTypes.Role, user.Role)
            }, "job"));
            context.Request.Method = "POST";
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("api.bantera.app");
            context.Request.ContentType = "application/json";
            var body = Encoding.UTF8.GetBytes(input.Body);
            context.Request.Body = new MemoryStream(body);
            context.Request.ContentLength = body.Length;
            context.Request.RouteValues = new RouteValueDictionary(input.RouteValues);
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
            await using var output = new OperationOutput(async events => {
                var encrypted = cipher.Seal(JsonSerializer.Serialize(events, OperationService.Json), job.Id);
                await using var progressScope = scopes.CreateAsyncScope();
                await progressScope.ServiceProvider.GetRequiredService<AppDbContext>().AiOperations
                    .Where(x => x.Id == job.Id && x.Status == "running")
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Events, encrypted), lifetime.Token);
            }, () => context.Response.ContentType?.StartsWith("text/event-stream") == true);
            context.Response.Body = output;
            accessor.HttpContext = context;
            await handler.Execute(context);
            await output.FlushAsync(lifetime.Token);
            var terminal = output.Events.LastOrDefault(e => e.Data.TryGetProperty("step", out var step) && step.GetString() is "done" or "error");
            var failed = context.Response.StatusCode >= 400 || (handler.Streaming &&
                (terminal is null || terminal.Data.GetProperty("step").GetString() != "done"));
            var result = handler.Streaming ? terminal?.Data.GetRawText() : output.JsonBody;
            if (string.IsNullOrWhiteSpace(result) || !ValidJson(result)) result = JsonSerializer.Serialize(new { message = OperationService.Failure });
            var encryptedResult = cipher.Seal(result, job.Id);
            var status = failed ? "failed" : "done";
            var statusCode = failed && context.Response.StatusCode < 400 ? 503 : context.Response.StatusCode;
            await db.AiOperations.Where(x => x.Id == job.Id && x.Status == "running").ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status).SetProperty(x => x.StatusCode, statusCode)
                .SetProperty(x => x.Output, encryptedResult).SetProperty(x => x.Input, (string?)null)
                .SetProperty(x => x.CompletedAt, DateTime.UtcNow), CancellationToken.None);
        } catch (Exception ex) {
            logs.LogWarning("AI operation {Id} ({Kind}) failed: {Type}", job.Id, job.Kind, ex.GetType().Name);
            var result = cipher.Seal(JsonSerializer.Serialize(new { message = ex is OperationCanceledException ? OperationService.Interrupted : OperationService.Failure }), job.Id);
            await db.AiOperations.Where(x => x.Id == job.Id && x.Status == "running").ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "failed").SetProperty(x => x.StatusCode, 503).SetProperty(x => x.Output, result)
                .SetProperty(x => x.Input, (string?)null).SetProperty(x => x.CompletedAt, DateTime.UtcNow), CancellationToken.None);
        } finally {
            accessor.HttpContext = null;
            await lifetime.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
        return true;
    }
    private async Task MaintainLease(AiOperation job, OperationHandler handler, CancellationTokenSource lifetime)
    {
        try {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(lifetime.Token)) {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var active = await db.Users.AnyAsync(u => u.Id == job.UserId && u.Status == "active" && u.DeletedAt == null && (!handler.Admin || u.Role == "admin"), lifetime.Token);
                if (!active || await db.AiOperations.Where(x => x.Id == job.Id && x.Status == "running")
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, DateTime.UtcNow.AddSeconds(60)), lifetime.Token) == 0) {
                    await lifetime.CancelAsync(); return;
                }
            }
        } catch (OperationCanceledException) { }
        catch { await lifetime.CancelAsync(); }
    }
    private static bool ValidJson(string text) { try { using var _ = JsonDocument.Parse(text); return true; } catch { return false; } }
    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}

// Persist SSE milestones, not heartbeat comments. Bound memory and stored output.
internal sealed class OperationOutput(Func<List<OperationEvent>, Task> persist, Func<bool> streaming) : MemoryStream
{
    private long consumed;
    private int saved;
    public List<OperationEvent> Events { get; } = [];
    public string JsonBody => Encoding.UTF8.GetString(ToArray());
    public override void Write(byte[] buffer, int offset, int count) { Bound(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Bound(buffer.Length); base.Write(buffer); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask;
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask;
    }
    private void Bound(int count) { if (Length + count > 4 * 1024 * 1024) throw new IOException("Operation result exceeds limit."); }
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!streaming()) return;
        var bytes = ToArray();
        var start = (int)consumed;
        for (var i = start; i + 1 < bytes.Length; i++) {
            if (bytes[i] != '\n' || bytes[i + 1] != '\n') continue;
            var frame = Encoding.UTF8.GetString(bytes, start, i - start);
            if (frame.StartsWith("data: ", StringComparison.Ordinal)) {
                if (Events.Count >= 64) throw new IOException("Too many operation events.");
                using var doc = JsonDocument.Parse(frame[6..]);
                Events.Add(new(Events.Count + 1, doc.RootElement.Clone()));
            }
            start = i + 2; consumed = start; i++;
        }
        if (saved != Events.Count) { await persist(Events); saved = Events.Count; }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BanteraApi.Auth;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Gemini;
using BanteraApi.Jobs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class AiOperationTests
{
    [Fact]
    public void EncryptionBindsJobAndRejectsTampering()
    {
        var cipher = new OperationCipher(Options.Create(new JwtSettings { Secret = new string('x', 48) }));
        var id = Guid.NewGuid(); var sealedText = cipher.Seal("private conversation", id);
        Assert.DoesNotContain("private", sealedText);
        Assert.Equal("private conversation", cipher.Open(sealedText, id));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => cipher.Open(sealedText, Guid.NewGuid()));
    }
    [Fact]
    public async Task ProgressHandlesSplitUtf8AndIgnoresHeartbeats()
    {
        var saves = 0;
        await using var output = new OperationOutput(_ => { saves++; return Task.CompletedTask; }, () => true);
        var bytes = Encoding.UTF8.GetBytes(": keep-alive\n\ndata: {\"step\":\"dialogue\",\"text\":\"你好\"}\n\n");
        for (var i = 0; i < bytes.Length; i++) {
            await output.WriteAsync(bytes.AsMemory(i, 1)); await output.FlushAsync();
        }
        Assert.Equal(1, saves); Assert.Single(output.Events);
        Assert.Equal("你好", output.Events[0].Data.GetProperty("text").GetString());
    }

    [OperationDatabaseFact]
    public async Task DurableQueuePreservesContractsOwnershipIdempotencyAndRestartSafety()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_OPERATION_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection); Assert.Contains("bantera_timeout_verify", connection);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddSingleton<IOptions<JwtSettings>>(Options.Create(new JwtSettings { Secret = new string('t', 48) }));
        builder.Services.AddSingleton<OperationCipher>(); builder.Services.AddSingleton<OperationRegistry>();
        builder.Services.AddScoped<OperationService>(); builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, WordActivityTests.TestAuth>("test", _ => {});
        builder.Services.AddAuthorization();
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization();
        OperationService.Map(app);
        var calls = 0;
        app.MapAiOperationPost("/slow/{videoId:guid}", "test", async (Guid videoId, TestBody body, HttpContext ctx, CancellationToken ct) => {
            Interlocked.Increment(ref calls);
            await Task.Delay(body.Delay, ct);
            return Results.Ok(new { body.Text, videoId, user = ctx.User.FindFirst("sub")!.Value });
        }).RequireAuthorization();
        app.MapAiOperationPost("/stream", "test-stream", async (TestBody body, HttpContext ctx, CancellationToken ct) => {
            ctx.Response.ContentType = "text/event-stream";
            await using var stream = new GenerationProgressStream(ctx.Response, ct);
            await stream.SendAsync(new { step = "dialogue", text = body.Text });
            await Task.Delay(body.Delay, ct);
            await stream.SendAsync(new { step = "done", video = new { title = body.Text } });
        }, streaming: true).RequireAuthorization();
        await app.StartAsync();
        var owner = Guid.NewGuid(); var other = Guid.NewGuid(); var video = Guid.NewGuid();
        await using (var seed = app.Services.CreateAsyncScope()) {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(new User { Id = owner, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }, new User { Id = other, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromMinutes(4) };
        client.DefaultRequestHeaders.Add("X-Test-User", owner.ToString());
        async Task<HttpResponseMessage> Post(Guid id, string text = "hello", string? path = null, int delay = 1, bool async = true) {
            var request = new HttpRequestMessage(HttpMethod.Post, path ?? $"/slow/{video}") { Content = JsonContent.Create(new TestBody(text, delay)) };
            request.Headers.Add("Idempotency-Key", id.ToString()); if (async) request.Headers.Add("Prefer", "respond-async");
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
        var worker = new OperationWorker(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OperationWorker>.Instance);
        try {
            var id = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.Accepted, (await Post(id)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await Post(id)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await Post(id, "different")).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-User"); client.DefaultRequestHeaders.Add("X-Test-User", other.ToString());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/me/ai-jobs/{id}")).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-User"); client.DefaultRequestHeaders.Add("X-Test-User", owner.ToString());
            Assert.True(await worker.RunOneAsync(CancellationToken.None)); Assert.Equal(1, calls);
            var state = await client.GetFromJsonAsync<JsonElement>($"/api/me/ai-jobs/{id}");
            Assert.Equal("done", state.GetProperty("status").GetString());
            Assert.Equal(video, state.GetProperty("result").GetProperty("videoId").GetGuid());
            Assert.Equal(owner.ToString(), state.GetProperty("result").GetProperty("user").GetString());
            Assert.Equal(HttpStatusCode.Accepted, (await Post(id)).StatusCode);
            Assert.False(await worker.RunOneAsync(CancellationToken.None)); Assert.Equal(1, calls);
            // Old JSON clients still receive their original result shape.
            var legacy = Post(Guid.NewGuid(), async: false);
            await Task.Delay(100); await worker.RunOneAsync(CancellationToken.None);
            var response = await legacy; Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("hello", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());
            // Cancellation removes payload and stops queued work.
            var cancelled = Guid.NewGuid(); await Post(cancelled);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/me/ai-jobs/{cancelled}")).StatusCode);
            Assert.False(await worker.RunOneAsync(CancellationToken.None));
            // Stale running work is terminal, not paid for again after a restart.
            var interrupted = Guid.NewGuid(); await Post(interrupted);
            await using (var scope = app.Services.CreateAsyncScope()) {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.AiOperations.Where(x => x.Id == interrupted).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "running").SetProperty(x => x.LeaseUntil, DateTime.UtcNow.AddMinutes(-2)));
            }
            Assert.False(await worker.RunOneAsync(CancellationToken.None));
            state = await client.GetFromJsonAsync<JsonElement>($"/api/me/ai-jobs/{interrupted}"); Assert.Equal("failed", state.GetProperty("status").GetString());
            // A long provider operation continues while the progress connection closes.
            var slow = Guid.NewGuid(); var slowResponse = await Post(slow, path: "/stream", delay: 130000, async: false);
            Assert.Equal("text/event-stream", slowResponse.Content.Headers.ContentType!.MediaType);
            var work = worker.RunOneAsync(CancellationToken.None);
            using (var reader = new StreamReader(await slowResponse.Content.ReadAsStreamAsync())) {
                var first = await reader.ReadLineAsync(); Assert.Contains("started", first);
                var heartbeatFound = false; var until = DateTime.UtcNow.AddSeconds(23);
                while (DateTime.UtcNow < until) {
                    var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    if (line?.StartsWith(": keep-alive") == true) { heartbeatFound = true; break; }
                }
                Assert.True(heartbeatFound);
            }
            slowResponse.Dispose();
            Assert.True(await work);
            state = await client.GetFromJsonAsync<JsonElement>($"/api/me/ai-jobs/{slow}");
            Assert.Equal("done", state.GetProperty("status").GetString());
            Assert.Equal(2, state.GetProperty("events").GetArrayLength());
        } finally {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(x => x.Id == owner || x.Id == other).ExecuteDeleteAsync();
            await app.StopAsync();
        }
    }
    public sealed record TestBody(string Text, int Delay);
}
public sealed class OperationDatabaseFactAttribute : FactAttribute
{
    public OperationDatabaseFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BANTERA_OPERATION_TEST_DB"))) Skip = "Requires disposable local timeout database."; }
}

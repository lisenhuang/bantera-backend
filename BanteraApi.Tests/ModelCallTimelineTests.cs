using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Diagnostics;
using BanteraApi.Gemini;
using BanteraApi.OpenAi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class ModelCallTimelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GptSuccessAndTransientRetryRecordSeparateCorrelatedCalls(bool retry)
    {
        using var fixture = new Fixture();
        var attempts = 0;
        using var http = new HttpClient(new Handler((request, ct) => {
            if (request.RequestUri!.AbsolutePath.EndsWith("models"))
                return Task.FromResult(Json("{\"models\":[{\"slug\":\"test-luna\",\"supported_reasoning_levels\":[\"high\"]}]}"));
            attempts++;
            if (retry && attempts == 1) return Task.FromResult(Json("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"server_error\",\"message\":\"private data\"}}}\n"));
            return Task.FromResult(Json("""
                data: {"type":"response.created"}
                data: {"type":"response.output_item.added","item":{"type":"reasoning"}}
                data: {"type":"response.output_text.delta","delta":"private answer"}
                data: {"type":"response.output_text.delta","delta":" continued"}
                data: {"type":"response.completed","response":{"status":"completed","output":[]}}
                """));
        }));
        using var operation = fixture.Events.BeginOperation("dialogue", "chatgpt/test-luna");
        using var context = fixture.Events.BeginContext(null, fixture.JobId, "en", "test");
        var result = await fixture.Client(http).CompleteAsync("test-luna", "high", "private prompt", "private instructions", false, default);
        Assert.Equal("private answer continued", result.Answer);
        var starts = fixture.Rows.Where(r => r.Code == "model_call_started").ToArray();
        Assert.Equal(retry ? 2 : 1, starts.Length);
        Assert.Equal(starts.Length, starts.Select(r => Data(r).GetProperty("callId").GetString()).Distinct().Count());
        var succeeded = Assert.Single(fixture.Rows, r => r.Code == "model_call_succeeded");
        Assert.Equal(Data(starts.Last()).GetProperty("callId").GetString(), Data(succeeded).GetProperty("callId").GetString());
        Assert.Single(fixture.Rows, r => r.Code == "model_call_progress" && Data(r).GetProperty("stage").GetString() == "answer_streaming");
        Assert.Contains(fixture.Rows, r => r.Code == "model_call_progress" && Data(r).GetProperty("stage").GetString() == "reasoning");
        Assert.All(fixture.Rows, r => { Assert.Equal(fixture.JobId, r.JobId); Assert.DoesNotContain("private", r.DetailJson); });
        Assert.Equal("high", Data(starts[0]).GetProperty("reasoning").GetString());
    }

    [Fact]
    public async Task GptCallerDeadlineRecordsAnOutcomeWithoutMisreportingTheGptDeadline()
    {
        using var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, ct) => { stop.Cancel(); return Task.FromCanceled<HttpResponseMessage>(ct); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client(http).CompleteAsync("test-luna", "high", "private", "private", false, stop.Token));
        var failed = Assert.Single(fixture.Rows, r => r.Code == "model_call_failed");
        Assert.Equal("cancelled_or_deadline", Data(failed).GetProperty("reason").GetString());
        Assert.DoesNotContain(fixture.Rows, r => r.Code == "response_timeout");
    }

    [Theory]
    [InlineData(200, "model_call_succeeded")]
    [InlineData(429, "model_call_failed")]
    public async Task GeminiCallsPreserveResponseAndNeverRecordUrlsBodiesOrKeys(int status, string expected)
    {
        using var fixture = new Fixture();
        using var handler = new GeminiModelCallHandler(fixture.Events) { InnerHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private response") })) };
        using var client = new HttpClient(handler);
        using (fixture.Events.BeginOperation("transcription", "test-transcribe")) {
            using var response = await client.PostAsync("https://example.test/v1beta/interactions?key=private", new StringContent("private audio"));
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("private response", await response.Content.ReadAsStringAsync());
        }
        Assert.Null(fixture.Events.CurrentOperation);
        var first = Assert.Single(fixture.Rows, r => r.Code == "model_call_started");
        var last = Assert.Single(fixture.Rows, r => r.Code == expected);
        Assert.Equal(Data(first).GetProperty("callId").GetString(), Data(last).GetProperty("callId").GetString());
        Assert.All(fixture.Rows, r => { Assert.Equal("transcription", r.Stage); Assert.DoesNotContain("private", r.DetailJson); });
    }

    [Fact]
    public async Task VoiceCatalogLookupsAreNotReportedAsGenerationCalls()
    {
        using var fixture = new Fixture();
        using var handler = new GeminiModelCallHandler(fixture.Events) { InnerHandler = new Handler((_, _) => Task.FromResult(Json("{}"))) };
        using var client = new HttpClient(handler);
        using var operation = fixture.Events.BeginOperation("tts", "test-tts");
        using var response = await client.GetAsync("https://example.test/v1beta/voices");
        Assert.Empty(fixture.Rows);
    }

    [Fact]
    public async Task OperationContextsAreIsolatedAcrossConcurrentCallsAndRestoreTheirParent()
    {
        using var fixture = new Fixture();
        using var parent = fixture.Events.BeginOperation("dialogue", "parent");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async i => {
            using var child = fixture.Events.BeginOperation("tts", "child" + i);
            await Task.Yield();
            Assert.Equal("child" + i, fixture.Events.CurrentOperation?.Model);
        }));
        Assert.Equal("parent", fixture.Events.CurrentOperation?.Model);
    }

    private static JsonElement Data(AiPipelineEvent row) => JsonDocument.Parse(row.DetailJson!).RootElement.Clone();
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class CaptureDb(List<AiPipelineEvent> rows) : AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused_trace_test").Options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken ct = default) {
            rows.AddRange(ChangeTracker.Entries<AiPipelineEvent>().Select(e => e.Entity));
            ChangeTracker.Clear();
            return Task.FromResult(1);
        }
    }
    private sealed class Fixture : IDisposable
    {
        public List<AiPipelineEvent> Rows { get; } = [];
        public Guid JobId { get; } = Guid.NewGuid();
        public AiPipelineEventRecorder Events { get; }
        private readonly ServiceProvider services;
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        private readonly string directory = Path.Combine(Path.GetTempPath(), "bantera-trace-" + Guid.NewGuid());
        private readonly ChatGptConnectionOptions config;
        public Fixture() {
            var collection = new ServiceCollection();
            collection.AddScoped<AppDbContext>(_ => new CaptureDb(Rows));
            services = collection.BuildServiceProvider();
            Events = new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AiPipelineEventRecorder>.Instance);
            cache.Set("ai-model-settings", new AiModelSelection("chatgpt/test-luna", "tts", null, null));
            cache.Set("chatgpt-codex-client-version", "1.0.0");
            var key = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(directory);
            config = new() { EncryptionKey = Convert.ToBase64String(key), StorageDirectory = directory };
            var account = new ChatGptDeviceCredential("private-account", null, "private-token", "private-refresh", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(directory, "codex-account.enc"), Convert.ToBase64String(ChatGptConnection.Seal(JsonSerializer.SerializeToUtf8Bytes(account), "codex-account", key)));
        }
        public ChatGptSubscriptionClient Client(HttpClient http) => new(http, new ChatGptConnection(http, Options.Create(config)), cache,
            new AiModelSettingsService(services.GetRequiredService<IServiceScopeFactory>(), cache, Options.Create(new GeminiSettings()), NullLogger<AiModelSettingsService>.Instance), Events);
        public void Dispose() { services.Dispose(); cache.Dispose(); Directory.Delete(directory, true); }
    }
}

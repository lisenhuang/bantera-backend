using System.Net;
using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Diagnostics;
using BanteraApi.Practice;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BanteraApi.Tests;
public class PracticeDiagnosticsTests
{
    [Fact]
    public async Task FailureKeepsCorrelationStatusAndDurationWithoutRawMessages()
    {
        var rows = new List<AiPipelineEvent>();
        using var services = new ServiceCollection().AddScoped<AppDbContext>(_ => new CaptureDb(rows)).BuildServiceProvider();
        var events = new AiPipelineEventRecorder(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AiPipelineEventRecorder>.Instance);
        var diagnostics = new PracticeDiagnostics(events);
        var context = new PracticeLogContext(Guid.NewGuid(), "zh-HK", 1, Guid.NewGuid(), 2);
        var error = new InvalidOperationException("private transcript and secret=abc",
            new HttpRequestException("https://private.example?key=secret", null, HttpStatusCode.TooManyRequests));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostics.RunAsync<int>("audio", context, async () =>
        {
            await events.RecordAsync("warning", "tts", "key_quota_cooldown", model: "test-model");
            throw error;
        }));
        Assert.Same(error, thrown);
        Assert.All(rows, row => { Assert.Equal(context.RunId, row.JobId); Assert.Equal("zh-HK", row.LanguageCode); Assert.Null(row.UserId); });
        var failure = Assert.Single(rows.Where(x => x.Code == "failed"));
        Assert.Equal("practice_audio", failure.Stage);
        Assert.NotNull(failure.DurationMs);
        Assert.Contains("429", failure.DetailJson);
        var json = JsonSerializer.Serialize(rows);
        Assert.DoesNotContain("private transcript", json);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("private.example", json);
        Assert.Null(events.CurrentUserId);
    }

    [Fact]
    public async Task DiagnosticsStorageFailureCannotFailSuccessfulPreparation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var recorder = new AiPipelineEventRecorder(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AiPipelineEventRecorder>.Instance);
        Assert.Equal(7, await new PracticeDiagnostics(recorder).RunAsync("text", new(Guid.NewGuid(), "en-NZ", 0), () => Task.FromResult(7)));
    }

    private sealed class CaptureDb(List<AiPipelineEvent> rows) : AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused_practice_test").Options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            rows.AddRange(ChangeTracker.Entries<AiPipelineEvent>().Select(x => x.Entity));
            ChangeTracker.Clear();
            return Task.FromResult(1);
        }
    }
}

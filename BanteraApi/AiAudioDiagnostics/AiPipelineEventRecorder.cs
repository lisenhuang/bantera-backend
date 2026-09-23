using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Diagnostics;

public static class AiPipelineSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
}

/// <summary>
/// Records AI pipeline events to ai_pipeline_events. Never throws: a diagnostics write must
/// not fail a generation. The user / job / language of the generation in progress flow in
/// through <see cref="BeginContext"/>, so deep code (key rotation, encoder) needs no plumbing.
/// </summary>
public sealed class AiPipelineEventRecorder(IServiceScopeFactory scopeFactory, ILogger<AiPipelineEventRecorder> logger)
{
    private sealed record Context(Guid? UserId, Guid? JobId, string? LanguageCode, string Endpoint);

    private static readonly AsyncLocal<Context?> Current = new();
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public IDisposable BeginContext(Guid? userId, Guid? jobId, string? languageCode, string endpoint)
    {
        var previous = Current.Value;
        Current.Value = new Context(userId, jobId, languageCode, endpoint);
        return new Restore(previous);
    }

    public async Task RecordAsync(
        string severity,
        string stage,
        string code,
        string? message = null,
        object? detail = null,
        string? model = null,
        string? keyHint = null,
        int? durationMs = null)
    {
        var context = Current.Value;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiPipelineEvents.Add(new AiPipelineEvent
            {
                Severity = severity,
                Stage = Truncate(stage, 30)!,
                Code = Truncate(code, 60)!,
                UserId = context?.UserId,
                JobId = context?.JobId,
                Endpoint = Truncate(context?.Endpoint, 60),
                LanguageCode = Truncate(context?.LanguageCode, 16),
                Model = Truncate(model, 100),
                KeyHint = Truncate(keyHint, 20),
                Message = Truncate(message, 2000),
                DetailJson = detail is null ? null : JsonSerializer.Serialize(detail, JsonOpts),
                DurationMs = durationMs,
            });
            // Not the request's token: record even when the generation timed out.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record AI pipeline event {Stage}/{Code}.", stage, code);
        }
    }

    public async Task<int> PurgeOlderThanAsync(TimeSpan age, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = DateTime.UtcNow - age;
        return await db.AiPipelineEvents.Where(e => e.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    private sealed class Restore(Context? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>Deletes pipeline events older than 90 days, once a day.</summary>
public sealed class AiPipelineEventCleanupService(AiPipelineEventRecorder recorder, ILogger<AiPipelineEventCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                var deleted = await recorder.PurgeOlderThanAsync(TimeSpan.FromDays(90), stoppingToken);
                if (deleted > 0) logger.LogInformation("Deleted {Count} AI pipeline events older than 90 days.", deleted);
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AI pipeline event cleanup failed.");
            }
        }
    }
}

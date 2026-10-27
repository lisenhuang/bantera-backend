using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Diagnostics;
using BanteraApi.Gemini;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Admin;

/// <summary>Claims provider work from the durable private queue. No provider keys leave the API.</summary>
public sealed class LocalModelGenerationWorker(IServiceScopeFactory scopes, IHttpClientFactory clients,
    IConfiguration config, ILogger<LocalModelGenerationWorker> logger) : BackgroundService
{
    private sealed record Claim(Guid Id, Guid Owner, string ClaimToken, int WordCount);
    private async Task<JsonElement?> Call(string path, object data, Guid owner, string? claim, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config["LocalModelTests:BaseUrl"]!.TrimEnd('/') + path);
        request.Headers.Add("X-Service-Key", config["LocalModelTests:ServiceKey"]);
        request.Headers.Add("X-Owner-Id", owner.ToString());
        if (claim is not null) request.Headers.Add("X-Generation-Claim", claim);
        request.Content = JsonContent.Create(data);
        using var response = await clients.CreateClient("local-model-tests").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) throw new OperationCanceledException("Generation claim ended.", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(config["LocalModelTests:BaseUrl"]) && !string.IsNullOrWhiteSpace(config["LocalModelTests:ServiceKey"]))
                {
                    var claimed = await Call("/generation/claim", new { }, Guid.Empty, null, stop);
                    if (claimed is { ValueKind: JsonValueKind.Object } c)
                    {
                        await Generate(new(c.GetProperty("id").GetGuid(), c.GetProperty("owner").GetGuid(),
                            c.GetProperty("claim").GetString()!, c.GetProperty("wordCount").GetInt32()), stop);
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Local test generation queue unavailable ({Type}).", ex.GetType().Name); }
            try { await Task.Delay(2000, stop); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task Generate(Claim job, CancellationToken stop)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lifetime.CancelAfter(TimeSpan.FromMinutes(10));
        var ct = lifetime.Token;
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Heartbeat(job, lifetime, heartbeatStop.Token);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.Users.AnyAsync(u => u.Id == job.Owner && u.Role == "admin" && u.Status == "active" && u.DeletedAt == null, ct))
                throw new UnauthorizedAccessException();
            using var context = scope.ServiceProvider.GetRequiredService<AiPipelineEventRecorder>()
                .BeginContext(job.Owner, null, "mi-NZ", "admin_model_tests");
            var ai = scope.ServiceProvider.GetRequiredService<GeminiService>();
            var clock = Stopwatch.StartNew();
            var text = await ai.GenerateModelTestPassageAsync(job.WordCount, ct);
            await Call($"/generation/{job.Id}/text", new { text.Maori, text.English, text.WordCount, text.ActualTextModel,
                text.ConfiguredTextModel, text.FallbackTextModel, textSeconds = clock.Elapsed.TotalSeconds }, job.Owner, job.ClaimToken, ct);
            var settings = await scope.ServiceProvider.GetRequiredService<AiModelSettingsService>().GetAsync(ct);
            clock.Restart();
            var audio = await ai.GenerateModelTestAudioAsync(text.Maori, ct);
            if (audio.DurationMs is < 200 or > 60000 || audio.Bytes.Length > 8 * 1024 * 1024)
                throw new InvalidDataException("generation_audio");
            // Stop heartbeats before the terminal hand-off, so a successful claim release cannot cancel itself.
            await heartbeatStop.CancelAsync(); await heartbeat;
            await Call($"/generation/{job.Id}/prepared", new { audioBase64 = Convert.ToBase64String(audio.Bytes),
                audioDurationMs = audio.DurationMs, audioSeconds = clock.Elapsed.TotalSeconds,
                configuredAudioModel = settings.AudioModel, fallbackAudioModel = settings.FallbackAudioModel,
                actualAudioModel = audio.ActualModel, audio.Voice, audioContentType = audio.ContentType }, job.Owner, job.ClaimToken, ct);
        }
        catch (Exception ex)
        {
            var code = ex is ModelTestWordCountException ? "word_count" : ex is OperationCanceledException ? "generation_timeout" :
                ex is InvalidDataException { Message: "generation_audio" } ? "generation_audio" : "generation_failed";
            logger.LogWarning("Local generated test {Id} stopped ({Code}, {Type}).", job.Id, code, ex.GetType().Name);
            try { using var end = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await Call($"/generation/{job.Id}/failed", new { error = code }, job.Owner, job.ClaimToken, end.Token); }
            catch (Exception) { /* Cancelled/expired claims cannot be revived; the service expires stale leases. */ }
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
    }
    private async Task Heartbeat(Claim job, CancellationTokenSource lifetime, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(2000, stop);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (!await db.Users.AnyAsync(u => u.Id == job.Owner && u.Role == "admin" && u.Status == "active" && u.DeletedAt == null, stop))
                { await lifetime.CancelAsync(); return; }
                await Call($"/generation/{job.Id}/heartbeat", new { }, job.Owner, job.ClaimToken, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) { await lifetime.CancelAsync(); }
    }
}

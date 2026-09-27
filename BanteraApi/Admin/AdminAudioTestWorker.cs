using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Gemini;
using BanteraApi.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BanteraApi.Admin;

/// <summary>Persistent queue, independent of browser lifetime. Never retries interrupted provider calls.</summary>
public sealed class AdminAudioTestWorker(IServiceScopeFactory scopes, ILogger<AdminAudioTestWorker> logger)
    : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                // Never log provider payloads or credentials outside the sanitized admin record.
                logger.LogError("Audio test worker could not process its queue ({ExceptionType}).", error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> RunNextAsync(CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // A killed process cannot complete its job. A stale attempt is failed, never replayed.
        var staleBefore = DateTime.UtcNow.AddMinutes(-15);
        var now = DateTime.UtcNow;
        await db.AdminAudioTests.Where(t => t.Status == "running" && t.StartedAt < staleBefore)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "failed")
                .SetProperty(t => t.CompletedAt, now)
                .SetProperty(t => t.ErrorJson, "{\"message\":\"The worker stopped or exceeded its time limit. This attempt was not retried.\"}"), stoppingToken);
        var id = await db.AdminAudioTests.AsNoTracking().Where(t => t.Status == "queued")
            .OrderBy(t => t.CreatedAt).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(stoppingToken);
        if (id is null) return false;
        var claimed = await db.AdminAudioTests.Where(t => t.Id == id && t.Status == "queued")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "running").SetProperty(t => t.StartedAt, now), stoppingToken);
        if (claimed == 0) return true;

        var test = await db.AdminAudioTests.SingleAsync(t => t.Id == id, stoppingToken);
        var geminiSettings = scope.ServiceProvider.GetRequiredService<IOptions<GeminiSettings>>().Value;
        var storageSettings = scope.ServiceProvider.GetRequiredService<IOptions<R2Settings>>().Value;
        var session = new GeminiTestSession(test.TextModel, test.AudioModel,
            [.. geminiSettings.ApiKeys, storageSettings.AccessKeyId, storageSettings.SecretAccessKey]);
        session.PersistAsync = async () =>
        {
            test.DiagnosticsJson = session.ToJson();
            await db.SaveChangesAsync(CancellationToken.None);
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        try
        {
            var gemini = scope.ServiceProvider.GetRequiredService<GeminiService>();
            var dialogue = test.DialogueJson is null ? null :
                JsonSerializer.Deserialize<GeneratedDialogue>(test.DialogueJson, JsonOptions);
            if (dialogue is null)
            {
                test.Stage = "dialogue";
                await db.SaveChangesAsync(ct);
                dialogue = await gemini.GenerateDialogueAsync(test.Language, test.LanguageCode, "",
                    test.TargetDurationSeconds, cancellationToken: ct, testSession: session);
                if (dialogue.Lines.Length == 0) throw new InvalidOperationException("The model returned an empty dialogue.");
                test.DialogueJson = JsonSerializer.Serialize(dialogue, JsonOptions);
                test.Title = dialogue.Title;
            }
            test.Stage = "tts";
            await db.SaveChangesAsync(ct);
            var audio = await gemini.GenerateAudioAsync(dialogue, test.LanguageCode, ct, session);
            if (audio.Bytes.Length == 0 || audio.DurationMs <= 0)
                throw new InvalidOperationException("The model returned empty or invalid audio.");
            test.Stage = "upload";
            test.AudioDurationMs = audio.DurationMs;
            test.AudioBytes = audio.Bytes.LongLength;
            await db.SaveChangesAsync(ct);
            var key = $"admin-audio-tests/{test.Id}{audio.FileExtension}";
            using var audioStream = new MemoryStream(audio.Bytes);
            await scope.ServiceProvider.GetRequiredService<R2StorageService>()
                .UploadObjectAsync(key, audioStream, audio.ContentType, ct);
            test.AudioObjectKey = key;
            test.AudioContentType = audio.ContentType;
            test.Stage = "done";
            test.Status = "done";
        }
        catch (Exception error)
        {
            test.Status = "failed";
            test.ErrorJson = session.ErrorJson(error, test.Stage);
        }
        test.DiagnosticsJson = session.ToJson();
        test.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }
}

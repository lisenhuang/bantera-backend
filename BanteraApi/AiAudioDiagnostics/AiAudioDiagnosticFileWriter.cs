using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BanteraApi.Diagnostics;

public class AiAudioDiagnosticFileWriter(
    IWebHostEnvironment environment,
    IOptions<AiAudioDiagnosticsOptions> options,
    ILogger<AiAudioDiagnosticFileWriter> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public Task WriteGenerationFailureAsync(object payload, CancellationToken cancellationToken = default) =>
        WriteAsync("last-ai-audio-generation-failure.json", payload, cancellationToken);

    public Task WriteShortCueNullAsync(object payload, CancellationToken cancellationToken = default) =>
        WriteAsync("last-short-cue-null.json", payload, cancellationToken);

    public Task WriteRevAiAlignmentFailureAsync(object payload, CancellationToken cancellationToken = default) =>
        WriteAsync("last-rev-ai-alignment-failure.json", payload, cancellationToken);

    public Task WriteShortCueValidationFailureAsync(object payload, CancellationToken cancellationToken = default) =>
        WriteAsync("last-short-cue-validation-failure.json", payload, cancellationToken);

    private async Task WriteAsync(string fileName, object payload, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
            return;

        try
        {
            var diagnosticsDirectoryName = string.IsNullOrWhiteSpace(options.Value.Directory)
                ? "diagnostics"
                : options.Value.Directory.Trim();
            var diagnosticsDirectoryPath = Path.Combine(environment.ContentRootPath, diagnosticsDirectoryName);
            Directory.CreateDirectory(diagnosticsDirectoryPath);

            var targetPath = Path.Combine(diagnosticsDirectoryPath, fileName);
            var tempPath = $"{targetPath}.tmp";
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken);
            File.Move(tempPath, targetPath, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to write AI audio diagnostic file '{FileName}'", fileName);
        }
    }
}

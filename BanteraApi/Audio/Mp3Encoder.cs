using System.Diagnostics;
using System.Globalization;

namespace BanteraApi.Audio;

/// <summary>
/// Encodes 16-bit mono PCM to MP3 by piping it through the `lame` CLI (installed in the
/// Docker image), or `ffmpeg` when only that is available. Returns null when no encoder
/// exists or encoding fails, so callers can keep the audio as WAV instead of failing.
/// </summary>
public sealed class Mp3Encoder(ILogger<Mp3Encoder> logger)
{
    public const string ContentType = "audio/mpeg";
    private const int BitrateKbps = 64; // plenty for speech; ~0.5 MB per minute
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private static readonly Lazy<(string Path, bool IsLame)?> Encoder = new(FindEncoder);

    public bool IsAvailable => Encoder.Value is not null;

    public async Task<byte[]?> EncodeAsync(byte[] pcm16Mono, int sampleRate, CancellationToken cancellationToken)
    {
        if (Encoder.Value is not { } encoder)
        {
            logger.LogWarning("No MP3 encoder (lame or ffmpeg) found on PATH; keeping audio as WAV.");
            return null;
        }

        var startInfo = new ProcessStartInfo(encoder.Path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in encoder.IsLame ? LameArgs(sampleRate) : FfmpegArgs(sampleRate))
            startInfo.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            var output = new MemoryStream();
            var readOutput = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            var readErrors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.BaseStream.WriteAsync(pcm16Mono, timeout.Token);
            process.StandardInput.Close();

            await readOutput;
            var errors = await readErrors;
            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0 || output.Length == 0)
            {
                logger.LogWarning("MP3 encoding failed (exit {ExitCode}): {Errors}", process.ExitCode, errors.Trim());
                return null;
            }
            return output.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "MP3 encoding failed; keeping audio as WAV.");
            return null;
        }
        finally
        {
            TryKill(process);
        }
    }

    private static IEnumerable<string> LameArgs(int sampleRate) =>
    [
        "-r", "-s", (sampleRate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture),
        "--bitwidth", "16", "--signed", "--little-endian", "-m", "m",
        "-b", BitrateKbps.ToString(CultureInfo.InvariantCulture), "--quiet", "-", "-",
    ];

    private static IEnumerable<string> FfmpegArgs(int sampleRate) =>
    [
        "-hide_banner", "-loglevel", "error",
        "-f", "s16le", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "1", "-i", "pipe:0",
        "-codec:a", "libmp3lame", "-b:a", $"{BitrateKbps}k", "-f", "mp3", "pipe:1",
    ];

    private static (string Path, bool IsLame)? FindEncoder()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var (name, isLame) in new[] { ("lame", true), ("ffmpeg", false) })
        {
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? name + ".exe" : name);
                if (File.Exists(candidate)) return (candidate, isLame);
            }
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }
}

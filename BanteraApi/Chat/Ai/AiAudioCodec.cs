using System.Diagnostics;

namespace BanteraApi.Chat.Ai;

public static class AiAudioCodec
{
    public static async Task<byte[]> DecodeAsync(IFormFile file, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bantera-ai-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input");
            var output = Path.Combine(directory, "output.pcm");
            await using (var stream = File.Create(input)) await file.CopyToAsync(stream, ct);
            var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file,pipe", "-i", input,
                         "-vn", "-t", "61", "-ac", "1", "-ar", "16000", "-f", "s16le", output }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Audio decoder unavailable.");
            try
            {
                var errors = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                await errors;
                if (process.ExitCode != 0 || !File.Exists(output)) throw new InvalidDataException("Invalid audio.");
                var pcm = await File.ReadAllBytesAsync(output, ct);
                if (pcm.Length < 320 || pcm.Length > 16000 * 2 * 60) throw new InvalidDataException("Invalid audio length.");
                return pcm;
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public static byte[] Wave(byte[] pcm, int rate = 24000)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
        return stream.ToArray();
    }
}

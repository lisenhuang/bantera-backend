using System.Buffers.Binary;

namespace BanteraApi.Chat.Ai;

public static class AiAudioCodec
{
    private const int BytesPerSecond = 16000 * 2;
    // Allow a little recorder-stop latency, but send at most 180 seconds to Live.
    private const int MaxPcmBytes = BytesPerSecond * 181;
    private const int MaxWaveBytes = MaxPcmBytes + 65536;

    public static async Task<byte[]> ReadPcmAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length < 44 || file.Length > MaxWaveBytes)
            throw new InvalidDataException("Invalid audio length.");
        var wave = new byte[(int)file.Length];
        await using var input = file.OpenReadStream();
        try { await input.ReadExactlyAsync(wave, ct); }
        catch (EndOfStreamException) { throw new InvalidDataException("Incomplete audio."); }
        return ReadWave(wave);
    }

    private static byte[] ReadWave(ReadOnlySpan<byte> wave)
    {
        if (!wave[..4].SequenceEqual("RIFF"u8) || !wave.Slice(8, 4).SequenceEqual("WAVE"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(wave[4..]) != wave.Length - 8)
            throw new InvalidDataException("Invalid WAV container.");

        var hasFormat = false;
        var dataOffset = -1;
        var dataLength = 0;
        for (var offset = 12; offset < wave.Length;)
        {
            if (wave.Length - offset < 8) throw new InvalidDataException("Incomplete WAV chunk.");
            var id = wave.Slice(offset, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(wave[(offset + 4)..]);
            offset += 8;
            var paddedLength = (long)length + (length & 1);
            if (paddedLength > wave.Length - offset) throw new InvalidDataException("Invalid WAV chunk length.");
            var chunk = wave.Slice(offset, (int)length);
            if (id.SequenceEqual("fmt "u8))
            {
                if (hasFormat || length < 16
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) != 1
                    || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 16000
                    || BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]) != BytesPerSecond
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]) != 2
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) != 16)
                    throw new InvalidDataException("Expected mono PCM16 audio at 16 kHz.");
                hasFormat = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (dataOffset >= 0 || length < 320 || length > MaxPcmBytes || (length & 1) != 0)
                    throw new InvalidDataException("Invalid audio length.");
                dataOffset = offset;
                dataLength = (int)length;
            }
            // Native recorders may include JUNK, LIST, fact or other metadata chunks.
            offset += (int)paddedLength;
        }
        if (!hasFormat || dataOffset < 0) throw new InvalidDataException("Missing WAV audio.");
        return wave.Slice(dataOffset, Math.Min(dataLength, BytesPerSecond * 180)).ToArray();
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

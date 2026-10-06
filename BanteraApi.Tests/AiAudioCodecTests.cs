using System.Buffers.Binary;
using BanteraApi.Chat.Ai;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BanteraApi.Tests;

public class AiAudioCodecTests
{
    private static async Task<byte[]> Read(byte[] wave)
    {
        using var input = new MemoryStream(wave);
        return await AiAudioCodec.ReadPcmAsync(new FormFile(input, 0, input.Length, "audio", "message.wav"), default);
    }

    [Fact]
    public async Task PreservesSamplesAndSkipsPaddedNativeMetadata()
    {
        var pcm = Enumerable.Range(0, 640).Select(i => (byte)i).ToArray();
        var wave = AiAudioCodec.Wave(pcm, 16000);
        // Insert an odd-sized metadata chunk before data, including its RIFF padding.
        var nativeWave = new byte[wave.Length + 12];
        wave.AsSpan(0, 36).CopyTo(nativeWave);
        "JUNK"u8.CopyTo(nativeWave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(nativeWave.AsSpan(40), 3);
        nativeWave[44] = 123;
        wave.AsSpan(36).CopyTo(nativeWave.AsSpan(48));
        BinaryPrimitives.WriteUInt32LittleEndian(nativeWave.AsSpan(4), (uint)nativeWave.Length - 8);
        Assert.Equal(pcm, await Read(wave));
        Assert.Equal(pcm, await Read(nativeWave));
    }

    [Theory]
    [InlineData(20, 3)] // Floating point rather than integer PCM.
    [InlineData(22, 2)] // Stereo.
    [InlineData(24, 1)] // Wrong sample rate.
    [InlineData(28, 1)] // Inconsistent bytes per second.
    [InlineData(32, 4)] // Wrong block alignment.
    [InlineData(34, 8)] // Wrong bit depth.
    [InlineData(0, 0)] // Not RIFF (e.g. compressed M4A).
    [InlineData(8, 0)] // Not WAVE.
    [InlineData(4, 0)] // Wrong RIFF length.
    [InlineData(16, 255)] // Format chunk overruns file.
    [InlineData(40, 255)] // Data chunk overruns file.
    public async Task RejectsInvalidFormatOrContainer(int offset, byte value)
    {
        var wave = AiAudioCodec.Wave(new byte[640], 16000);
        wave[offset] = value;
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(wave));
    }

    [Fact]
    public async Task RejectsTruncatedMissingAndDuplicateChunks()
    {
        var wave = AiAudioCodec.Wave(new byte[640], 16000);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(wave[..^1]));
        var missing = wave.ToArray();
        "JUNK"u8.CopyTo(missing.AsSpan(36));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(missing));
        missing = wave.ToArray();
        "JUNK"u8.CopyTo(missing.AsSpan(12));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(missing));
        foreach (var duplicate in new[] { wave[12..36], wave[36..] })
        {
            var twice = wave.Concat(duplicate).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(twice.AsSpan(4), (uint)twice.Length - 8);
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(twice));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(318)]
    [InlineData(321)]
    [InlineData(16000 * 2 * 181 + 2)]
    public async Task RejectsInvalidSampleLength(int length)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(AiAudioCodec.Wave(new byte[length], 16000)));
    }

    [Fact]
    public async Task PreservesFullThreeMinuteMessage()
    {
        var pcm = new byte[16000 * 2 * 180];
        pcm[^2] = 42;
        Assert.Equal(pcm, await Read(AiAudioCodec.Wave(pcm, 16000)));
    }

    [Fact]
    public async Task AllowsStopLatencyButForwardsAtMostThreeMinutes()
    {
        var pcm = new byte[16000 * 2 * 181];
        pcm[0] = 42;
        var result = await Read(AiAudioCodec.Wave(pcm, 16000));
        Assert.Equal(16000 * 2 * 180, result.Length);
        Assert.Equal(42, result[0]);
    }
}

using System.Buffers.Binary;
using System.Text.Json;
using BanteraApi.Chat.Ai;
using BanteraApi.Database.Entities;
using BanteraApi.Gemini;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class AiVoiceActivityTests
{
    private static readonly AiClientMetadata Metadata = new(new("Pacific/Auckland", 780), null);

    [Theory]
    [InlineData(0)]
    [InlineData(320)]
    [InlineData(16000)]
    [InlineData(AiVoiceInput.MaxBytes)]
    public async Task EmptyAndSilentRecordingsCompleteAsNoSpeech(int length)
    {
        var input = new AiVoiceInput();
        Add(input, new byte[length]);
        Assert.False(input.SpeechDetected.IsCompleted);
        input.Commit(Metadata);
        Assert.False(await input.SpeechDetected);
    }

    [Theory]
    [InlineData("yes", 1.0, 2)]
    [InlineData("yes", 0.1, 638)]
    [InlineData("no", 1.0, 640)]
    [InlineData("no", 0.1, 3200)]
    public async Task ShortMeaningfulWordsAreDetectedBeforeSend(string word, double scale, int chunk)
    {
        var input = new AiVoiceInput();
        var pcm = Word(word);
        Assert.InRange(pcm.Length, 1, 31999); // Still displayed as 0 seconds by old clients.
        for (var i = 0; i < pcm.Length; i += 2)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), (short)(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i)) * scale));
        Add(input, pcm, chunk);
        Assert.True(await input.SpeechDetected.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(input.Committed.IsCompleted); // Upload may begin while recording.
        input.Commit(Metadata);
        Assert.True(await input.SpeechDetected);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("dc")]
    [InlineData("quiet_noise")]
    [InlineData("noise")]
    [InlineData("hum")]
    [InlineData("tone")]
    public async Task NonSpeechDoesNotBecomeATurn(string kind)
    {
        var pcm = new byte[32000 * 3];
        var random = new Random(12);
        for (var i = 0; i < pcm.Length; i += 2) {
            var sample = kind switch {
                "click" => i is 1000 or 20000 ? 30000 : 0,
                "dc" => 2000,
                "quiet_noise" => random.Next(-10, 11),
                "hum" => (int)(2000 * Math.Sin(2 * Math.PI * 60 * (i / 2) / 16000)),
                "tone" => (int)(2000 * Math.Sin(2 * Math.PI * 440 * (i / 2) / 16000)),
                _ => random.Next(-2000, 2001)
            };
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), (short)sample);
        }
        var input = new AiVoiceInput(); Add(input, pcm); input.Commit(Metadata);
        Assert.False(await input.SpeechDetected);
    }

    [Fact]
    public async Task SilentRecordingCannotOpenProviderOrEmitToolsAudioOrTranscript()
    {
        // A null key service proves the no-speech outcome precedes any provider work.
        var live = new GeminiLiveService(Options.Create(new GeminiSettings()), null!, NullLogger<GeminiLiveService>.Instance);
        var input = new AiVoiceInput(); input.Add(new byte[640]);
        var reply = live.StreamReplyAsync("any-live-model", new User(), input, [], default, Metadata, "Puck",
            _ => throw new Exception("Must not execute a tool"), _ => throw new Exception("Must not emit audio"), default,
            (_, _) => throw new Exception("Must not emit a transcript"));
        Assert.False(reply.IsCompleted);
        input.Commit(Metadata);
        await Assert.ThrowsAsync<AiNoSpeechException>(() => reply);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"acceptNoSpeech\":false}", false)]
    [InlineData("{\"acceptNoSpeech\":true}", true)]
    [InlineData("{\"acceptNoSpeech\":\"true\"}", false)]
    public void NewTerminalStatusRequiresExplicitClientSupport(string start, bool supported)
    {
        using var json = JsonDocument.Parse(start);
        Assert.Equal(supported, AiVoiceMessageEndpoint.WantsNoSpeech(json.RootElement));
        var frame = JsonSerializer.SerializeToElement(AiVoiceMessageEndpoint.NoSpeechFrame(supported));
        Assert.Equal(supported ? "no_speech" : "error", frame.GetProperty("type").GetString());
    }

    private static void Add(AiVoiceInput input, byte[] pcm, int chunk = 16000)
    {
        for (var i = 0; i < pcm.Length; i += chunk) input.Add(pcm.AsSpan(i, Math.Min(chunk, pcm.Length - i)).ToArray());
    }
    private static byte[] Word(string word)
    {
        using var resource = typeof(AiVoiceActivityTests).Assembly.GetManifestResourceStream($"BanteraApi.Tests.Fixtures.Voice.{word}.pcm")!;
        using var output = new MemoryStream(); resource.CopyTo(output); return output.ToArray();
    }
}

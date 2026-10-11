using System.Buffers.Binary;
using WebRtcVad.NET;

namespace BanteraApi.Chat.Ai;

public sealed class AiNoSpeechException : Exception
{
    public AiNoSpeechException() : base("No speech detected in the recording.") { }
}

// PCM evidence, independent of Gemini's occasionally hallucinated transcription.
// The small managed WebRTC GMM works on the server's ARM64 runtime without a
// native binary, model download, or sending the recording to another provider.
public sealed class AiVoiceActivity
{
    private const int FrameBytes = 640; // 20 ms, mono PCM16, 16 kHz.
    private readonly WebRtcVad.NET.WebRtcVad vad = new() {
        SampleRate = SampleRate.Rate16kHz,
        FrameLength = FrameLength.Length20ms,
        OperatingMode = OperatingMode.Aggressive
    };
    private readonly byte[] frame = new byte[FrameBytes];
    private int filled, voicedFrames, quietFrames;
    private double minimumEnergy = double.MaxValue, maximumEnergy;
    public bool HasSpeech { get; private set; }

    public void Add(ReadOnlySpan<byte> pcm)
    {
        if (HasSpeech) return;
        while (!pcm.IsEmpty) {
            var take = Math.Min(FrameBytes - filled, pcm.Length);
            pcm[..take].CopyTo(frame.AsSpan(filled));
            filled += take;
            pcm = pcm[take..];
            if (filled != FrameBytes) continue;
            filled = 0;
            // Ignore DC offsets and VAD hangover on silent frames. A click must
            // not become speech merely because VAD stays active after its peak.
            double sum = 0, energy = 0;
            var crossings = 0;
            short previous = 0;
            for (var i = 0; i < frame.Length; i += 2) {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(i));
                sum += sample;
                energy += (double)sample * sample;
                if (i > 0 && (sample < 0) != (previous < 0)) crossings++;
                previous = sample;
            }
            const int samples = FrameBytes / 2;
            var variance = energy / samples - Math.Pow(sum / samples, 2);
            if (vad.HasSpeech(frame) && variance >= 16 * 16 && crossings < samples * 0.35) {
                quietFrames = 0;
                minimumEnergy = Math.Min(minimumEnergy, variance);
                maximumEnergy = Math.Max(maximumEnergy, variance);
                // 80 ms of actual voiced signal accepts a brief "yes"/"no"
                // while rejecting accidental taps; this is not message length.
                // WebRTC can label stationary startup noise as speech before
                // its noise estimate adapts. Also require modest speech-envelope
                // variation (2 dB), rather than accepting a steady fan/hum.
                if (++voicedFrames >= 4 && maximumEnergy >= minimumEnergy * 1.6) { HasSpeech = true; return; }
            } else if (++quietFrames >= 5) {
                voicedFrames = 0; minimumEnergy = double.MaxValue; maximumEnergy = 0;
            }
        }
    }
}

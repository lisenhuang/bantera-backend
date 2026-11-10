namespace BanteraApi.Chat.Ai;

// Relative monotonic timings only. The socket reader and provider writer can
// run concurrently, including during a retry, so take consistent snapshots.
public sealed class AiVoiceTiming(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly long started = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private readonly object gate = new();
    private long? ready, firstClientPcm, lastClientPcm, commit, speech, firstClientAudio;
    private int attempts;
    private long? attemptStart, providerReady, firstProviderPcm, providerInputEnd, firstProviderAudio, toolStart;
    private int providerBytesBeforeCommit, toolRounds;
    private long toolMs;
    private string? providerInputMode;

    private long Now => (long)clock.GetElapsedTime(started).TotalMilliseconds;

    internal void ClientReady() { lock (gate) ready = Now; }
    internal void ClientPcm()
    {
        lock (gate) {
            lastClientPcm = Now;
            firstClientPcm ??= lastClientPcm;
        }
    }
    internal void ClientCommit() { lock (gate) commit = Now; }
    internal void SpeechDetected() { lock (gate) speech = Now; }
    internal void ClientAudio() { lock (gate) firstClientAudio ??= Now; }

    internal void BeginAttempt()
    {
        lock (gate) {
            attempts++;
            attemptStart = Now;
            providerReady = firstProviderPcm = providerInputEnd = firstProviderAudio = toolStart = null;
            providerBytesBeforeCommit = toolRounds = 0;
            toolMs = 0;
            providerInputMode = null;
        }
    }
    internal void ProviderReady(string mode)
    {
        lock (gate) {
            providerReady = Now;
            providerInputMode = mode;
        }
    }
    internal void ProviderPcmSent(int bytes)
    {
        lock (gate) {
            firstProviderPcm ??= Now;
            if (commit is null) providerBytesBeforeCommit += bytes;
        }
    }
    internal void ProviderInputEnd() { lock (gate) providerInputEnd = Now; }
    internal void ProviderAudio() { lock (gate) firstProviderAudio ??= Now; }
    internal void ToolRoundStarted() { lock (gate) toolStart = Now; }
    internal void ToolRoundFinished()
    {
        lock (gate) {
            if (toolStart is not long start) return;
            toolRounds++;
            toolMs += Now - start;
            toolStart = null;
        }
    }

    internal object Snapshot()
    {
        lock (gate) return new {
            clientReadyMs = ready, firstClientPcmMs = firstClientPcm, lastClientPcmMs = lastClientPcm,
            clientCommitMs = commit, speechDetectedMs = speech, firstClientAudioSentMs = firstClientAudio,
            commitToFirstAudioMs = firstClientAudio - commit,
            providerAttempts = attempts,
            latestAttempt = attempts == 0 ? null : new {
                startMs = attemptStart, readyMs = providerReady, inputMode = providerInputMode,
                firstPcmSentMs = firstProviderPcm, pcmBytesSentBeforeCommit = providerBytesBeforeCommit,
                inputEndMs = providerInputEnd, firstAudioMs = firstProviderAudio,
                inputEndToFirstAudioMs = firstProviderAudio - providerInputEnd,
                toolRounds, toolMs, unfinishedToolMs = toolStart is long start ? Now - start : (long?)null
            }
        };
    }
}

namespace BanteraApi.Chat.Ai;

public sealed class AiLiveResponseTimeoutException : TimeoutException
{
    public AiLiveResponseTimeoutException(bool canReplay = true) : base("Live response stalled.") { CanReplay = canReplay; }
    public bool CanReplay { get; }
}

// Recording time is not response time. Start the idle deadline only after Send,
// and renew it for audio progress, not provider keepalives or empty transcripts.
public sealed class AiReplyWatchdog : IAsyncDisposable
{
    private readonly CancellationTokenSource stop;
    private readonly CancellationToken parent;
    private readonly TimeSpan idle;
    private readonly TimeProvider clock;
    private readonly ITimer deadline;
    private readonly Task arm;
    private long? playbackStarted;
    private double audioSeconds;
    public AiReplyWatchdog(Task committed, CancellationToken parent, TimeSpan? idle = null, TimeProvider? timeProvider = null)
    {
        this.parent = parent;
        this.idle = idle ?? TimeSpan.FromSeconds(20);
        clock = timeProvider ?? TimeProvider.System;
        stop = CancellationTokenSource.CreateLinkedTokenSource(parent);
        deadline = clock.CreateTimer(_ => stop.Cancel(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        arm = ArmAsync(committed);
    }
    public CancellationToken Token => stop.Token;
    public bool TimedOut => stop.IsCancellationRequested && !parent.IsCancellationRequested;
    // Gemini generates PCM faster than playback and may withhold turnComplete
    // until that audio would have finished playing. Waiting for buffered speech
    // to finish is not a stalled provider. The parent still bounds the whole turn.
    public void AudioProgress(int bytes)
    {
        if (bytes <= 2) return; // Do not treat the resume keepalive as speech.
        playbackStarted ??= clock.GetTimestamp();
        audioSeconds += bytes / 48000d;
        Progress();
    }
    public void ResetAudio() { audioSeconds = 0; playbackStarted = null; Progress(); }
    public void Progress()
    {
        var elapsed = playbackStarted is long started ? clock.GetElapsedTime(started).TotalSeconds : 0;
        deadline.Change(idle + TimeSpan.FromSeconds(Math.Max(0, audioSeconds - elapsed)), Timeout.InfiniteTimeSpan);
    }
    private async Task ArmAsync(Task committed)
    {
        try { await committed.WaitAsync(stop.Token); Progress(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await arm;
        await deadline.DisposeAsync();
        stop.Dispose();
    }
}

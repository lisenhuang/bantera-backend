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
    private readonly Task arm;
    private readonly System.Diagnostics.Stopwatch playback = new();
    private double audioSeconds;
    public AiReplyWatchdog(Task committed, CancellationToken parent, TimeSpan? idle = null)
    {
        this.parent = parent;
        this.idle = idle ?? TimeSpan.FromSeconds(20);
        stop = CancellationTokenSource.CreateLinkedTokenSource(parent);
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
        if (!playback.IsRunning) playback.Start();
        audioSeconds += bytes / 48000d;
        Progress();
    }
    public void ResetAudio() { audioSeconds = 0; playback.Reset(); Progress(); }
    public void Progress() => stop.CancelAfter(idle + TimeSpan.FromSeconds(
        Math.Max(0, audioSeconds - playback.Elapsed.TotalSeconds)));
    private async Task ArmAsync(Task committed)
    {
        try { await committed.WaitAsync(stop.Token); Progress(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await arm;
        stop.Dispose();
    }
}

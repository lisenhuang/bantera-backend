namespace BanteraApi.Chat.Ai;

public sealed class AiLiveResponseTimeoutException : TimeoutException
{
    public AiLiveResponseTimeoutException() : base("Live response stalled.") { }
}

// Recording time is not response time. Start the idle deadline only after Send,
// and renew it for audio progress, not provider keepalives or empty transcripts.
public sealed class AiReplyWatchdog : IAsyncDisposable
{
    private readonly CancellationTokenSource stop;
    private readonly CancellationToken parent;
    private readonly TimeSpan idle;
    private readonly Task arm;
    public AiReplyWatchdog(Task committed, CancellationToken parent, TimeSpan? idle = null)
    {
        this.parent = parent;
        this.idle = idle ?? TimeSpan.FromSeconds(20);
        stop = CancellationTokenSource.CreateLinkedTokenSource(parent);
        arm = ArmAsync(committed);
    }
    public CancellationToken Token => stop.Token;
    public bool TimedOut => stop.IsCancellationRequested && !parent.IsCancellationRequested;
    public void Progress() => stop.CancelAfter(idle);
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

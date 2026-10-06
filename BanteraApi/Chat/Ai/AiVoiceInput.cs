using System.Runtime.CompilerServices;

namespace BanteraApi.Chat.Ai;

// A bounded, in-memory recording. Each provider attempt can replay from the beginning
// while new microphone chunks continue arriving. Nothing is persisted on the server.
public sealed class AiVoiceInput
{
    public const int MaxBytes = 16000 * 2 * 180;
    private readonly object gate = new();
    private readonly List<byte[]> chunks = [];
    private TaskCompletionSource changed = NewSignal();
    private readonly TaskCompletionSource<AiClientMetadata> committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int length;
    private bool complete;
    public Task<AiClientMetadata> Committed => committed.Task;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Add(byte[] pcm)
    {
        lock (gate) {
            if (complete || pcm.Length == 0 || pcm.Length > 32000 || pcm.Length % 2 != 0 || length + pcm.Length > MaxBytes)
                throw new InvalidDataException("Invalid recording audio.");
            chunks.Add(pcm); length += pcm.Length;
            var previous = changed; changed = NewSignal(); previous.TrySetResult();
        }
    }
    public void Commit(AiClientMetadata metadata)
    {
        lock (gate) {
            if (complete || length < 320) throw new InvalidDataException("Invalid recording length.");
            complete = true; committed.TrySetResult(metadata); changed.TrySetResult();
        }
    }
    public async IAsyncEnumerable<byte[]> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var index = 0;
        while (true) {
            byte[]? chunk; Task wait; bool done;
            lock (gate) {
                chunk = index < chunks.Count ? chunks[index++] : null;
                done = complete; wait = changed.Task;
            }
            ct.ThrowIfCancellationRequested();
            if (chunk is not null) yield return chunk;
            else if (done) yield break;
            else await wait.WaitAsync(ct);
        }
    }
}

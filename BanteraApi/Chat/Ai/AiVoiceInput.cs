using System.Runtime.CompilerServices;

namespace BanteraApi.Chat.Ai;

// A bounded, in-memory recording. Each provider attempt can replay from the beginning
// while new microphone chunks continue arriving. Nothing is persisted on the server.
public sealed class AiVoiceInput
{
    public AiVoiceInput(IReadOnlyList<AiVoiceImage>? images = null, bool textOnly = false)
    {
        Images = images?.ToArray() ?? [];
        TextOnly = textOnly;
        if (textOnly && Images.Count > 0) throw new InvalidDataException("Text input cannot include recording images.");
    }
    public bool TextOnly { get; }
    public string? Text { get; private set; }
    public const int MaxTextLength = 8000;
    public IReadOnlyList<AiVoiceImage> Images { get; }
    public const int MaxBytes = 16000 * 2 * 180;
    private readonly object gate = new();
    private readonly List<byte[]> chunks = [];
    private TaskCompletionSource changed = NewSignal();
    private readonly TaskCompletionSource<AiClientMetadata> committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> speechDetected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AiVoiceActivity activity = new();
    private int length;
    private bool complete;
    public Task<AiClientMetadata> Committed => committed.Task;
    public Task<bool> SpeechDetected => speechDetected.Task;
    public int ByteCount { get { lock (gate) return length; } }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Add(byte[] pcm)
    {
        lock (gate) {
            if (TextOnly || complete || pcm.Length == 0 || pcm.Length > 32000 || pcm.Length % 2 != 0 || length + pcm.Length > MaxBytes)
                throw new InvalidDataException("Invalid recording audio.");
            chunks.Add(pcm); length += pcm.Length;
            activity.Add(pcm);
            if (activity.HasSpeech) speechDetected.TrySetResult(true);
            var previous = changed; changed = NewSignal(); previous.TrySetResult();
        }
    }
    public void Commit(AiClientMetadata metadata, string? text = null)
    {
        lock (gate) {
            if (complete) throw new InvalidDataException("Recording already committed.");
            if (TextOnly ? string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength : text is not null)
                throw new InvalidDataException("Invalid text input.");
            Text = text;
            complete = true; committed.TrySetResult(metadata);
            // Explicit text mode bypasses VAD only after a valid committed text turn.
            speechDetected.TrySetResult(TextOnly || activity.HasSpeech);
            changed.TrySetResult();
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

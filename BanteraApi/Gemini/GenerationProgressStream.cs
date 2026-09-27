using System.Text.Json;

namespace BanteraApi.Gemini;

/// <summary>Keeps long generation requests alive without cancelling work when the client leaves.</summary>
public sealed class GenerationProgressStream : IAsyncDisposable
{
    private readonly HttpResponse response;
    private readonly CancellationTokenSource stopped;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly Task heartbeat;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public GenerationProgressStream(HttpResponse response, CancellationToken disconnected)
    {
        this.response = response;
        stopped = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
        heartbeat = KeepAliveAsync();
    }

    public Task SendAsync(object payload) => WriteAsync($"data: {JsonSerializer.Serialize(payload, Options)}\n\n");

    private async Task WriteAsync(string text)
    {
        try
        {
            await writer.WaitAsync(stopped.Token);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await response.WriteAsync(text, timeout.Token);
                await response.Body.FlushAsync(timeout.Token);
            }
            finally { writer.Release(); }
        }
        catch { await stopped.CancelAsync(); } // The job's lifetime is independent of the stream.
    }

    private async Task KeepAliveAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(stopped.Token))
                await WriteAsync(": keep-alive\n\n");
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await stopped.CancelAsync();
        await heartbeat;
        stopped.Dispose();
        writer.Dispose();
    }
}

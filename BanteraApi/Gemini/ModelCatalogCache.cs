namespace BanteraApi.Gemini;

internal static class ModelCatalogCache
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static GeminiModelCatalog? value;
    private static DateTime refreshed;
    public static async Task<GeminiModelCatalog> GetAsync(Func<CancellationToken, Task<GeminiModelCatalog>> fetch, CancellationToken ct)
    {
        if (value is not null && DateTime.UtcNow - refreshed < TimeSpan.FromMinutes(5)) return value;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        try {
            await Gate.WaitAsync(budget.Token);
            try {
                if (value is not null && DateTime.UtcNow - refreshed < TimeSpan.FromMinutes(5)) return value;
                var result = await fetch(budget.Token);
                value = result; refreshed = DateTime.UtcNow;
                return result;
            } finally { Gate.Release(); }
        } catch (Exception) when (!ct.IsCancellationRequested && value is not null && DateTime.UtcNow - refreshed < TimeSpan.FromDays(1)) {
            return value;
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            throw new HttpRequestException("Model catalogue refresh timed out.");
        }
    }
}

using System.Diagnostics;

namespace BanteraApi.Diagnostics;

/// <summary>Records each HTTP call within a generation operation, including retries and polling.
/// Never records request URLs, headers, prompts, response bodies or credentials.</summary>
public sealed class GeminiModelCallHandler(AiPipelineEventRecorder events) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var isGeneration = path.EndsWith(":generateContent", StringComparison.Ordinal)
            || path == "/v1beta/interactions" || path.StartsWith("/v1beta/interactions/", StringComparison.Ordinal);
        if (!isGeneration || events.CurrentOperation is not { } operation)
            return await base.SendAsync(request, ct);

        var callId = Guid.NewGuid().ToString("N");
        var kind = request.Method == HttpMethod.Get ? "poll" : "generate";
        var clock = Stopwatch.StartNew();
        await events.RecordAsync("info", operation.Operation, "model_call_started", "Provider request started.",
            new { callId, provider = "gemini", kind }, operation.Model);
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, ct);
            // Match HttpClient's normal buffered response contract, so elapsed time includes the body.
            await response.Content.LoadIntoBufferAsync(ct);
            await events.RecordAsync(response.IsSuccessStatusCode ? "info" : "warning", operation.Operation,
                response.IsSuccessStatusCode ? "model_call_succeeded" : "model_call_failed",
                response.IsSuccessStatusCode ? "Provider response received; pipeline validation follows." : "Provider rejected the request.",
                new { callId, provider = "gemini", kind, httpStatus = (int)response.StatusCode }, operation.Model,
                durationMs: (int)Math.Min(clock.ElapsedMilliseconds, int.MaxValue));
            return response;
        }
        catch (Exception ex)
        {
            response?.Dispose();
            await events.RecordAsync("warning", operation.Operation, "model_call_failed", "Provider request did not complete.",
                new { callId, provider = "gemini", kind, reason = ex is OperationCanceledException ? "cancelled_or_deadline" : "transport_error" },
                operation.Model, durationMs: (int)Math.Min(clock.ElapsedMilliseconds, int.MaxValue));
            throw;
        }
    }
}

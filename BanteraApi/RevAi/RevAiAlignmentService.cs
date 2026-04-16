using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BanteraApi.Videos;
using Microsoft.Extensions.Options;

namespace BanteraApi.RevAi;

public class RevAiAlignmentService(
    IHttpClientFactory httpClientFactory,
    IOptions<RevAiSettings> options,
    ILogger<RevAiAlignmentService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SupportedPrimaryLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en",
        "fr",
        "de",
        "it",
        "es",
    };

    public static bool IsRevAiSupported(string languageCode)
    {
        var primary = (languageCode ?? string.Empty)
            .Replace('_', '-')
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return !string.IsNullOrWhiteSpace(primary)
            && SupportedPrimaryLanguages.Contains(primary);
    }

    public async Task<IReadOnlyList<WordTimingRecord>> AlignAsync(
        string sourceUrl,
        string transcript,
        CancellationToken cancellationToken = default)
    {
        var accessToken = options.Value.AccessToken.Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Rev.ai access token is not configured.");

        var client = httpClientFactory.CreateClient("revai");
        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, "/speechtotext/v1/jobs");
        submitRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        submitRequest.Content = new StringContent(
            JsonSerializer.Serialize(
                new
                {
                    source_config = new { url = sourceUrl },
                    alignment_config = new { transcript },
                    metadata = "bantera-ai-audio-v2",
                },
                JsonOpts),
            Encoding.UTF8,
            "application/json");

        using var submitResponse = await client.SendAsync(submitRequest, cancellationToken);
        var submitJson = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        submitResponse.EnsureSuccessStatusCode();

        using var submitDoc = JsonDocument.Parse(submitJson);
        var jobId = submitDoc.RootElement.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("Rev.ai did not return a job id.");

        await WaitForJobAsync(client, accessToken, jobId, cancellationToken);
        return await FetchTranscriptAsync(client, accessToken, jobId, cancellationToken);
    }

    private async Task WaitForJobAsync(
        HttpClient client,
        string accessToken,
        string jobId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/speechtotext/v1/jobs/{jobId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await client.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            if (string.Equals(status, "transcribed", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Rev.ai alignment job failed.");

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        throw new TimeoutException("Rev.ai alignment job timed out.");
    }

    private async Task<IReadOnlyList<WordTimingRecord>> FetchTranscriptAsync(
        HttpClient client,
        string accessToken,
        string jobId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/speechtotext/v1/jobs/{jobId}/transcript");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.rev.transcript.v1.0+json"));

        using var response = await client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);
        var timings = new List<WordTimingRecord>();
        CollectWordTimings(doc.RootElement, timings);

        var normalized = timings
            .Where(w => !string.IsNullOrWhiteSpace(w.Word) && w.EndMs > w.StartMs)
            .OrderBy(w => w.StartMs)
            .ToList();

        if (normalized.Count == 0)
            throw new InvalidOperationException("Rev.ai transcript did not contain word timings.");

        logger.LogInformation("Rev.ai returned {WordCount} aligned words for job {JobId}.", normalized.Count, jobId);
        return normalized;
    }

    private static void CollectWordTimings(JsonElement element, List<WordTimingRecord> output)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryReadWordTiming(element, out var timing))
                output.Add(timing);

            foreach (var property in element.EnumerateObject())
                CollectWordTimings(property.Value, output);
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectWordTimings(item, output);
        }
    }

    private static bool TryReadWordTiming(JsonElement element, out WordTimingRecord timing)
    {
        timing = new WordTimingRecord("", 0, 0, null);

        if (element.TryGetProperty("type", out var typeElement)
            && !string.Equals(typeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var word = ReadString(element, "value")
            ?? ReadString(element, "word")
            ?? ReadString(element, "text");
        if (string.IsNullOrWhiteSpace(word))
            return false;

        var startSeconds = ReadDouble(element, "ts")
            ?? ReadDouble(element, "start_ts")
            ?? ReadDouble(element, "start");
        var endSeconds = ReadDouble(element, "end_ts")
            ?? ReadDouble(element, "end");
        if (startSeconds is null || endSeconds is null)
            return false;

        var startMs = Math.Max(0, (int)Math.Round(startSeconds.Value * 1000));
        var endMs = Math.Max(startMs + 1, (int)Math.Round(endSeconds.Value * 1000));
        timing = new WordTimingRecord(
            word.Trim(),
            startMs,
            endMs,
            ReadDouble(element, "confidence"));
        return true;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var n) => n,
            JsonValueKind.String when double.TryParse(value.GetString(), out var n) => n,
            _ => null,
        };
    }
}

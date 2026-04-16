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
    private const int MaxLoggedBodyLength = 4000;
    private const string DebugSeparator = "===================================";
    private static readonly HashSet<string> SupportedPrimaryLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en",
        "fr",
        "de",
        "it",
        "es",
    };

    public static bool IsRevAiSupported(string languageCode) =>
        TryGetSupportedLanguageCode(languageCode, out _);

    public static bool TryGetSupportedLanguageCode(string languageCode, out string? canonicalLanguageCode)
    {
        var primary = (languageCode ?? string.Empty)
            .Replace('_', '-')
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim()
            .ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(primary)
            && SupportedPrimaryLanguages.Contains(primary))
        {
            canonicalLanguageCode = primary;
            return true;
        }

        canonicalLanguageCode = null;
        return false;
    }

    public async Task<IReadOnlyList<WordTimingRecord>> AlignAsync(
        string sourceUrl,
        string languageCode,
        string transcript,
        CancellationToken cancellationToken = default)
    {
        var accessToken = options.Value.AccessToken.Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Rev.ai access token is not configured.");

        var client = httpClientFactory.CreateClient("revai");
        var settings = options.Value;
        var diagnostics = RevAiTranscriptDiagnostics.Create(transcript, settings.TranscriptPreviewMaxChars);
        var submitBody = BuildSubmitBody(sourceUrl, languageCode, transcript);
        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, BuildSubmitPath());
        submitRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        submitRequest.Content = new StringContent(submitBody, Encoding.UTF8, "application/json");
        LogRequestDebugBlock(
            client,
            submitRequest,
            "submit",
            diagnostics,
            settings.LogTranscriptPreview ? diagnostics.TranscriptPreview : "(disabled)",
            settings.LogTranscriptFull ? transcript : "(disabled)",
            settings.LogDelimitedJsonDebug);
        LogSubmitTranscriptTextBlock(transcript, settings.LogDelimitedJsonDebug);

        using var submitResponse = await client.SendAsync(submitRequest, cancellationToken);
        var submitJson = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!submitResponse.IsSuccessStatusCode)
        {
            LogResponseDebugBlock(
                client,
                submitRequest,
                submitResponse,
                "submit",
                submitJson,
                settings.LogResponseBodyMaxChars,
                settings.LogDelimitedJsonDebug,
                true);
        }
        EnsureSuccess(
            submitResponse,
            "submit",
            submitJson,
            null);

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
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildStatusPath(jobId));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await client.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogResponseDebugBlock(
                    client,
                    request,
                    response,
                    "status",
                    json,
                    options.Value.LogResponseBodyMaxChars,
                    options.Value.LogDelimitedJsonDebug,
                    true);
            }
            EnsureSuccess(
                response,
                "status",
                json,
                jobId);

            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            if (IsCompletedStatus(status))
                return;

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                LogStatusFailedDebugBlock(
                    client,
                    request,
                    jobId,
                    json,
                    options.Value.LogResponseBodyMaxChars,
                    options.Value.LogDelimitedJsonDebug);
                throw new InvalidOperationException(
                    $"Rev.ai alignment job failed. jobId={jobId}, response={TruncateForLog(json)}");
            }

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
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildTranscriptPath(jobId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.rev.transcript.v1.0+json"));

        using var response = await client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogResponseDebugBlock(
                client,
                request,
                response,
                "transcript",
                json,
                options.Value.LogResponseBodyMaxChars,
                options.Value.LogDelimitedJsonDebug,
                true);
        }
        EnsureSuccess(
            response,
            "transcript",
            json,
            jobId);

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

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string stage,
        string body,
        string? jobId)
    {
        if (response.IsSuccessStatusCode)
            return;

        var statusCode = (int)response.StatusCode;
        var reason = response.ReasonPhrase ?? "unknown";
        var trimmedBody = TruncateForLog(body);
        throw new InvalidOperationException(
            $"Rev.ai {stage} request failed. status={statusCode}, reason={reason}, jobId={jobId ?? "n/a"}, response={trimmedBody}");
    }

    private static string TruncateForLog(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (text.Length <= MaxLoggedBodyLength)
            return text;

        return $"{text[..MaxLoggedBodyLength]}...";
    }

    private void LogRequestDebugBlock(
        HttpClient client,
        HttpRequestMessage request,
        string stage,
        RevAiTranscriptDiagnostics? diagnostics,
        string? transcriptPreview,
        string? transcriptFull,
        bool enabled)
    {
        if (!enabled)
            return;

        var payload = new
        {
            stage,
            type = "request",
            url = BuildFullUrl(client, request),
            method = request.Method.Method,
            headers = BuildRequestHeaders(request),
            body = request.Content is null ? null : request.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
            transcript = diagnostics is null
                ? null
                : new
                {
                    diagnostics.CharCount,
                    diagnostics.LineCount,
                    diagnostics.TranscriptHash,
                    diagnostics.NormalizedTranscriptHash,
                    transcriptPreview,
                    transcriptFull,
                }
        };

        logger.LogInformation("{Block}", CreateDelimitedJsonBlock("REVAI_REQUEST_DEBUG", payload));
    }

    private void LogResponseDebugBlock(
        HttpClient client,
        HttpRequestMessage request,
        HttpResponseMessage response,
        string stage,
        string responseBody,
        int maxChars,
        bool enabled,
        bool asError)
    {
        if (!enabled)
            return;

        var truncated = CreatePossiblyTruncatedBody(responseBody, maxChars);
        var payload = new
        {
            stage,
            type = "response",
            url = BuildFullUrl(client, request),
            method = request.Method.Method,
            statusCode = (int)response.StatusCode,
            reasonPhrase = response.ReasonPhrase ?? "unknown",
            headers = BuildResponseHeaders(response),
            body = truncated.body,
            bodyWasTruncated = truncated.wasTruncated,
            originalBodyLength = truncated.originalLength,
        };

        var block = CreateDelimitedJsonBlock("REVAI_RESPONSE_DEBUG", payload);
        if (asError)
            logger.LogError("{Block}", block);
        else
            logger.LogInformation("{Block}", block);
    }

    public static string CreateDelimitedJsonBlock(string label, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });
        return $"{DebugSeparator}\n{label}\n{json}\n{DebugSeparator}";
    }

    public static string CreateDelimitedPlainTextBlock(string content)
    {
        var safeContent = content ?? string.Empty;
        return $"{DebugSeparator}\nbelow is the text we send to revai:\n{safeContent}\n{DebugSeparator}";
    }

    public static string BuildSubmitPath() => "/alignment/v1/jobs";

    public static string BuildStatusPath(string jobId) => $"/alignment/v1/jobs/{jobId}";

    public static string BuildTranscriptPath(string jobId) => $"/alignment/v1/jobs/{jobId}/transcript";

    public static bool IsCompletedStatus(string? status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    public static string BuildSubmitBody(string sourceUrl, string languageCode, string transcript)
    {
        return JsonSerializer.Serialize(
            new
            {
                source_config = new { url = sourceUrl },
                transcript_text = transcript,
                language = languageCode,
            },
            JsonOpts);
    }

    public static (string body, bool wasTruncated, int originalLength) CreatePossiblyTruncatedBody(string body, int maxChars)
    {
        var safeBody = body ?? string.Empty;
        var safeMax = Math.Max(1, maxChars);
        if (safeBody.Length <= safeMax)
            return (safeBody, false, safeBody.Length);

        return ($"{safeBody[..safeMax]}...", true, safeBody.Length);
    }

    private static string BuildFullUrl(HttpClient client, HttpRequestMessage request)
    {
        var requestUri = request.RequestUri ?? new Uri("/", UriKind.Relative);
        var full = requestUri.IsAbsoluteUri
            ? requestUri
            : new Uri(client.BaseAddress ?? throw new InvalidOperationException("revai client BaseAddress is not set."), requestUri);
        return full.ToString();
    }

    private static Dictionary<string, string> BuildRequestHeaders(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
            headers[h.Key] = string.Join(", ", h.Value.Select(RedactSensitiveHeaderValue));

        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
                headers[h.Key] = string.Join(", ", h.Value);
        }

        return headers;
    }

    private static Dictionary<string, string> BuildResponseHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers)
            headers[h.Key] = string.Join(", ", h.Value);
        foreach (var h in response.Content.Headers)
            headers[h.Key] = string.Join(", ", h.Value);
        return headers;
    }

    public static string RedactSensitiveHeaderValue(string value)
    {
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return "Bearer [REDACTED]";
        return value;
    }

    private void LogSubmitTranscriptTextBlock(string transcript, bool enabled)
    {
        if (!enabled)
            return;

        logger.LogInformation("{Block}", CreateDelimitedPlainTextBlock(transcript));
    }

    private void LogStatusFailedDebugBlock(
        HttpClient client,
        HttpRequestMessage request,
        string jobId,
        string responseBody,
        int maxChars,
        bool enabled)
    {
        if (!enabled)
            return;

        var truncated = CreatePossiblyTruncatedBody(responseBody, maxChars);
        var payload = new
        {
            stage = "status",
            type = "response",
            status = "failed",
            jobId,
            url = BuildFullUrl(client, request),
            method = request.Method.Method,
            body = truncated.body,
            bodyWasTruncated = truncated.wasTruncated,
            originalBodyLength = truncated.originalLength,
        };

        logger.LogError("{Block}", CreateDelimitedJsonBlock("REVAI_RESPONSE_DEBUG", payload));
    }
}

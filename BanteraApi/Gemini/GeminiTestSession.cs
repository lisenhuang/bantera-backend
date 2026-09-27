using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BanteraApi.Gemini;

/// <summary>Admin-only diagnostics for a single attempt. Never stores audio data or API keys.</summary>
public sealed class GeminiTestSession(string textModel, string audioModel, IReadOnlyList<string> secrets)
{
    public string TextModel { get; } = textModel;
    public string AudioModel { get; } = audioModel;
    public List<object> Calls { get; } = [];
    public Func<Task>? PersistAsync { get; set; }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Sanitize(string value)
    {
        foreach (var secret in secrets.Where(s => !string.IsNullOrWhiteSpace(s)))
            value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        value = Regex.Replace(value, @"AIza[\w-]{20,}", "[redacted]");
        value = Regex.Replace(value, @"(?i)([?&]key=)[^&\s\""']+", "$1[redacted]");
        return Regex.Replace(value, @"(?i)(Bearer\s+)[\w.\-]+", "$1[redacted]");
    }

    public async Task<HttpResponseMessage> SendAsync(HttpClient client, string url, string stage,
        string model, string key, object request, CancellationToken ct)
    {
        var call = CreateCall(stage, model, key, request);
        Calls.Add(call);
        if (PersistAsync is not null) await PersistAsync();
        var clock = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");
            response = await client.PostAsync(url, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            CompleteCall(call, response, body);
            call.DurationMs = clock.ElapsedMilliseconds;
            if (PersistAsync is not null) await PersistAsync();
            return response;
        }
        catch (Exception error)
        {
            response?.Dispose();
            call.DurationMs = clock.ElapsedMilliseconds;
            call.TransportError = Sanitize(error.ToString());
            if (PersistAsync is not null) await PersistAsync();
            throw;
        }
    }

    public async Task CaptureAsync(string stage, string model, string key, object request,
        HttpResponseMessage response, string body)
    {
        var call = CreateCall(stage, model, key, request);
        CompleteCall(call, response, body);
        Calls.Add(call);
        if (PersistAsync is not null) await PersistAsync();
    }

    private ProviderCall CreateCall(string stage, string model, string key, object request) => new()
    {
        Stage = stage, Model = model, At = DateTime.UtcNow,
        KeyHint = key.Length > 10 ? $"{key[..6]}…{key[^4..]}" : "***",
        Request = JsonNode.Parse(Sanitize(JsonSerializer.Serialize(request, JsonOptions))),
    };

    private void CompleteCall(ProviderCall call, HttpResponseMessage response, string body)
    {
        JsonNode? payload;
        try
        {
            payload = JsonNode.Parse(body);
            RemoveAudio(payload);
        }
        catch (JsonException) { payload = JsonValue.Create(body); }
        var responseText = payload?.ToJsonString() ?? body;
        const int maxLength = 128_000;
        call.Headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key.Equals("retry-after", StringComparison.OrdinalIgnoreCase)
                || h.Key.Equals("date", StringComparison.OrdinalIgnoreCase)
                || h.Key.Contains("request-id", StringComparison.OrdinalIgnoreCase)
                || h.Key.Contains("trace", StringComparison.OrdinalIgnoreCase)
                || h.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => Sanitize(string.Join(", ", h.Value)));
        call.HttpStatus = (int)response.StatusCode;
        call.ReasonPhrase = response.ReasonPhrase;
        call.ResponseBody = Sanitize(responseText[..Math.Min(responseText.Length, maxLength)]);
        call.ResponseTruncated = responseText.Length > maxLength;
    }

    private sealed class ProviderCall
    {
        public required string Stage { get; init; }
        public required string Model { get; init; }
        public DateTime At { get; init; }
        public required string KeyHint { get; init; }
        public JsonNode? Request { get; init; }
        public long? DurationMs { get; set; }
        public int? HttpStatus { get; set; }
        public string? ReasonPhrase { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public string? ResponseBody { get; set; }
        public bool ResponseTruncated { get; set; }
        public string? TransportError { get; set; }
    }

    public string ToJson() => Sanitize(JsonSerializer.Serialize(Calls, JsonOptions));

    public string ErrorJson(Exception error, string stage) => Sanitize(JsonSerializer.Serialize(new
    {
        stage, at = DateTime.UtcNow, exception = error.GetType().FullName,
        message = error.Message, stackTrace = error.ToString(),
        httpStatus = FindHttpStatus(error),
    }, JsonOptions));

    private static int? FindHttpStatus(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is HttpRequestException { StatusCode: { } status })
                return (int)status;
        return null;
    }

    private static void RemoveAudio(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var entry in obj.ToArray())
            {
                if (entry.Key is "inlineData" or "inline_data" && entry.Value is JsonObject inline)
                {
                    if (inline["data"] is not null) inline["data"] = "[audio data omitted]";
                }
                else RemoveAudio(entry.Value);
            }
        }
        else if (node is JsonArray array)
            foreach (var item in array) RemoveAudio(item);
    }
}

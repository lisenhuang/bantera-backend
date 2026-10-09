using System.Net;
using BanteraApi.Gemini;
using BanteraApi.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace BanteraApi.OpenAi;

public sealed record ChatGptModel(string Id, string Name, string[] ReasoningLevels, string? DefaultReasoning);
public sealed record ChatGptSource(string Title, string Url);
public sealed record ChatGptTestResult(string Answer, bool SearchVerified, IReadOnlyList<ChatGptSource> Sources);

/// <summary>Codex subscription transport, isolated from the public API-key/SIWC transport.</summary>
public sealed class ChatGptSubscriptionClient(HttpClient http, ChatGptConnection connection, IMemoryCache cache, AiModelSettingsService modelSettings, AiPipelineEventRecorder events)
{
    private const string Base = "https://chatgpt.com/backend-api/codex/";
    private async Task<string> VersionAsync(CancellationToken ct)
    {
        if (cache.TryGetValue<string>("chatgpt-codex-client-version", out var existing)) return existing!;
        var data = await http.GetFromJsonAsync<JsonElement>("https://registry.npmjs.org/@openai/codex/latest", ct);
        var version = Text(data, "version");
        if (!System.Version.TryParse(version, out _)) throw new ChatGptConnectionException("catalog_unavailable");
        cache.Set("chatgpt-codex-client-version", version, TimeSpan.FromHours(6));
        return version;
    }
    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var account = await connection.GetDeviceCredentialAsync(ct);
        var version = await VersionAsync(ct);
        var request = new HttpRequestMessage(method, Base + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        request.Headers.Add("ChatGPT-Account-ID", account.AccountId);
        request.Headers.Add("originator", "codex_cli_rs");
        request.Headers.UserAgent.ParseAdd("codex_cli_rs/" + version + " (Bantera)");
        return request;
    }
    public async Task<IReadOnlyList<ChatGptModel>> ModelsAsync(CancellationToken ct)
    {
        using var catalogTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        catalogTimeout.CancelAfter(TimeSpan.FromSeconds(20)); ct = catalogTimeout.Token;
        using var request = await RequestAsync(HttpMethod.Get, "models?client_version=" + Uri.EscapeDataString(await VersionAsync(ct)), ct);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var data = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return ParseModels(data);
    }
    public static IReadOnlyList<ChatGptModel> ParseModels(JsonElement data)
    {
        var list = new List<ChatGptModel>();
        if (!data.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new ChatGptConnectionException("catalog_unavailable");
        foreach (var m in models.EnumerateArray()) {
            var id = Text(m, "slug"); var visibility = Text(m, "visibility");
            if (id == "" || visibility is not ("" or "list")) continue;
            var levels = m.TryGetProperty("supported_reasoning_levels", out var efforts) && efforts.ValueKind == JsonValueKind.Array
                ? efforts.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : Text(e, "effort"))
                    .Where(e => e.Length is > 0 and < 32 && e.All(c => char.IsAsciiLetter(c) || c == '_')).Distinct().ToArray() : [];
            var name = Text(m, "display_name"); var defaultEffort = Text(m, "default_reasoning_level");
            list.Add(new(id, name == "" ? id : name, levels, levels.Contains(defaultEffort) ? defaultEffort : null));
        }
        if (list.Count == 0) throw new ChatGptConnectionException("catalog_unavailable");
        return list;
    }
    public Task<ChatGptTestResult> TestAsync(string model, string? effort, string prompt, bool search, CancellationToken ct) =>
        CompleteAsync(model, effort, prompt, "You are Bantera's AI provider connection test. Answer the admin's request briefly and accurately.", search, ct);

    public async Task<ChatGptTestResult> CompleteAsync(string model, string? effort, string prompt, string instructions, bool search, CancellationToken ct)
    {
        var seconds = (await modelSettings.GetAsync(ct)).GptTimeoutSeconds;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var progress = new ResponseProgress();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try {
            return await RetryTransientAsync(async token => {
                progress = new ResponseProgress();
                try { return await CompleteCoreAsync(model, effort, prompt, instructions, search, progress, token); }
                catch (ChatGptConnectionException ex) {
                    await events.RecordAsync(AiPipelineSeverity.Warning, "chatgpt_response", "provider_response_failed",
                        "GPT could not complete its response.", new { ex.Code, ex.ProviderCode, ex.HttpStatus,
                            stage = ex.Stage ?? progress.Stage, searchCompleted = ex.SearchCompleted || progress.SearchCompleted,
                            answerCharacters = Math.Max(ex.AnswerCharacters, progress.AnswerCharacters) },
                        "chatgpt/" + model, durationMs: (int)elapsed.ElapsedMilliseconds);
                    throw;
                }
            }, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested) {
            await events.RecordAsync(AiPipelineSeverity.Warning, "chatgpt_response", "response_timeout",
                "GPT reached the configured response deadline.", new { seconds, progress.Stage, progress.SearchCompleted, progress.AnswerCharacters },
                "chatgpt/" + model, durationMs: (int)elapsed.ElapsedMilliseconds);
            throw new TimeoutException($"GPT response exceeded the configured {seconds}-second deadline.");
        }
    }

    // One retry only, inside the original deadline. Never retry quota, auth,
    // unsupported requests, caller cancellation, or a partially generated answer.
    public static async Task<ChatGptTestResult> RetryTransientAsync(Func<CancellationToken, Task<ChatGptTestResult>> attempt, CancellationToken ct)
    {
        try { return await attempt(ct); }
        catch (ChatGptConnectionException ex) when (ex.Code == "provider_failed" && ex.AnswerCharacters == 0 &&
            (ex.ProviderCode is "server_error" or "internal_error" or "internal_server_error" || ex.HttpStatus is >= 500 and <= 599)) {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            return await attempt(ct);
        }
    }
    private sealed class ResponseProgress
    {
        public string Stage { get; set; } = "connecting";
        public bool SearchCompleted { get; set; }
        public int AnswerCharacters { get; set; }
    }
    private async Task<ChatGptTestResult> CompleteCoreAsync(string model, string? effort, string prompt, string instructions, bool search, ResponseProgress progress, CancellationToken ct)
    {
        var selected = (await ModelsAsync(ct)).FirstOrDefault(m => m.Id == model)
            ?? throw new ChatGptConnectionException("invalid_model");
        if (!string.IsNullOrEmpty(effort) && !selected.ReasoningLevels.Contains(effort)) throw new ChatGptConnectionException("invalid_reasoning");
        using var request = await RequestAsync(HttpMethod.Post, "responses", ct);
        request.Headers.Accept.Add(new("text/event-stream"));
        var body = new Dictionary<string, object> {
            ["model"] = model, ["store"] = false, ["stream"] = true,
            ["instructions"] = instructions,
            ["input"] = new[] { new { role = "user", content = new[] { new { type = "input_text", text = prompt } } } }
        };
        if (!string.IsNullOrEmpty(effort)) body["reasoning"] = new { effort };
        if (search) { body["tools"] = new[] { new { type = "web_search" } }; body["tool_choice"] = "required"; body["include"] = new[] { "web_search_call.action.sources" }; }
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await ParseResponseCoreAsync(stream, progress, ct);
    }
    public static Task<ChatGptTestResult> ParseResponseAsync(Stream stream, CancellationToken ct) => ParseResponseCoreAsync(stream, new ResponseProgress(), ct);
    private static async Task<ChatGptTestResult> ParseResponseCoreAsync(Stream stream, ResponseProgress progress, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        var answer = new StringBuilder(); var sources = new List<ChatGptSource>(); bool searched = false;
        void Source(JsonElement a) {
            var url = Text(a, "url");
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo == "")
                sources.Add(new(Text(a, "title"), url));
        }
        void Item(JsonElement item) {
            if (Text(item, "type") == "web_search_call") {
                if (Text(item, "status") == "completed") searched = true;
                if (item.TryGetProperty("action", out var action) && action.TryGetProperty("sources", out var refs) && refs.ValueKind == JsonValueKind.Array)
                    foreach (var source in refs.EnumerateArray()) Source(source);
            }
            if (item.TryGetProperty("content", out var contents) && contents.ValueKind == JsonValueKind.Array)
                foreach (var part in contents.EnumerateArray())
                    if (part.TryGetProperty("annotations", out var refs) && refs.ValueKind == JsonValueKind.Array)
                        foreach (var source in refs.EnumerateArray()) if (Text(source, "type") == "url_citation") Source(source);
        }
        while (await reader.ReadLineAsync(ct) is { } line) {
            if (!line.StartsWith("data: ", StringComparison.Ordinal) || line == "data: [DONE]") continue;
            using var doc = JsonDocument.Parse(line.AsSpan(6).ToString()); var data = doc.RootElement; var type = Text(data, "type");
            // A fixed stage vocabulary only: never persist raw events, prompts, text or credentials.
            if (type == "response.created") progress.Stage = "created";
            if (type == "response.web_search_call.searching") progress.Stage = "searching";
            if (type == "response.web_search_call.completed") { searched = true; progress.Stage = "search_completed"; }
            if (type == "response.content_part.added") progress.Stage = "answer_started";
            if (type == "response.output_item.done" && data.TryGetProperty("item", out var doneItem)) Item(doneItem);
            if (type == "response.output_text.annotation.added" && data.TryGetProperty("annotation", out var annotation) && Text(annotation, "type") == "url_citation") Source(annotation);
            if (type == "response.output_text.delta") { answer.Append(Text(data, "delta")); progress.Stage = "answer_streaming"; }
            progress.SearchCompleted = searched; progress.AnswerCharacters = answer.Length;
            if (answer.Length > 100000) throw new ChatGptConnectionException("response_too_large");
            if (type is "error" or "response.failed" or "response.incomplete") {
                var envelope = data.TryGetProperty("response", out var failed) ? failed : data;
                var error = envelope.TryGetProperty("error", out var providerError) && providerError.ValueKind == JsonValueKind.Object ? providerError : envelope;
                var providerCode = SafeProviderCode(Text(error, "code"));
                throw new ChatGptConnectionException(providerCode is "rate_limit_exceeded" or "usage_limit_reached" or "insufficient_quota" ? "quota_exceeded" : "provider_failed") {
                    ProviderCode = providerCode, Stage = progress.Stage, SearchCompleted = searched, AnswerCharacters = answer.Length
                };
            }
            if (type != "response.completed") continue;
            if (!data.TryGetProperty("response", out var completed) || Text(completed, "status") != "completed")
                throw new ChatGptConnectionException("provider_failed");
            var finalText = new StringBuilder();
            if (completed.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                foreach (var item in output.EnumerateArray()) {
                    Item(item);
                    if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                    foreach (var part in content.EnumerateArray()) {
                        if (Text(part, "type") == "output_text") finalText.Append(Text(part, "text"));
                        if (!part.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array) continue;
                        foreach (var a in annotations.EnumerateArray()) {
                            var url = Text(a, "url");
                            if (Text(a, "type") == "url_citation" && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                                && uri.Scheme is "https" or "http" && uri.UserInfo == "") sources.Add(new(Text(a, "title"), url));
                        }
                    }
                }
            var text = finalText.Length > 0 ? finalText.ToString() : answer.ToString();
            if (string.IsNullOrWhiteSpace(text)) throw new ChatGptConnectionException("empty_response");
            return new(text, searched, sources.DistinctBy(s => s.Url).Take(20).ToArray());
        }
        throw new ChatGptConnectionException("response_incomplete");
    }
    private static string Text(JsonElement data, string key) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
    private static string? SafeProviderCode(string value) => value.Length is > 0 and <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? value : null;
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct) {
        if (response.IsSuccessStatusCode) return;
        // Do not copy the provider body/message into logs; it may echo private inputs.
        string? providerCode = null;
        if (response.Content.Headers.ContentLength is <= 16384) {
            try {
                var data = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error))
                    providerCode = SafeProviderCode(Text(error, "code"));
            } catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        }
        throw new ChatGptConnectionException(response.StatusCode switch {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "reconnect_required",
            HttpStatusCode.TooManyRequests => "quota_exceeded",
            HttpStatusCode.BadRequest => "unsupported_request",
            _ => "provider_failed"
        }) { HttpStatus = (int)response.StatusCode, ProviderCode = providerCode };
    }
}

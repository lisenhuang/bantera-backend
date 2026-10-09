using System.Net;
using System.Text.Json;
using BanteraApi.OpenAi;

namespace BanteraApi.Gemini;

public partial class GeminiService
{
    public static bool IsChatGpt(string model) => model.StartsWith("chatgpt/", StringComparison.Ordinal);

    // Preserve the validated Gemini response contract used by dialogue/correction/alignment.
    // Only text requests pass here; audio synthesis and transcription remain on Gemini.
    private async Task<HttpResponseMessage> SendTextRequestAsync(string model, string key, object body, CancellationToken ct,
        bool search = false, string? reasoning = null)
    {
        if (!IsChatGpt(model)) return await httpClientFactory.CreateClient("gemini").PostAsJsonAsync(
            $"/v1beta/models/{model}:generateContent?key={key}", body, JsonOpts, ct);
        if (chatGpt is null) throw new InvalidOperationException("ChatGPT provider is unavailable.");
        var payload = JsonSerializer.SerializeToElement(body, JsonOpts);
        var (prompt, instructions) = TextProviderPrompt(payload);
        var result = await chatGpt.CompleteAsync(model[8..], reasoning, prompt, instructions, search, ct);
        if (search && !result.SearchVerified) throw new ChatGptConnectionException("search_not_verified");
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new {
            candidates = new[] { new {
                finishReason = "STOP", content = new { parts = new[] { new { text = result.Answer } } },
                groundingMetadata = new { groundingChunks = result.Sources.Select(s => new { web = new { uri = s.Url, title = s.Title } }) }
            } }
        }, options: JsonOpts) };
    }

    public static (string Prompt, string Instructions) TextProviderPrompt(JsonElement payload)
    {
        static string Parts(JsonElement content) => content.TryGetProperty("parts", out var parts)
            ? string.Join("\n", parts.EnumerateArray().Where(p => p.TryGetProperty("text", out _)).Select(p => p.GetProperty("text").GetString())) : "";
        var prompt = string.Join("\n", payload.GetProperty("contents").EnumerateArray().Select(Parts));
        var instructions = payload.TryGetProperty("systemInstruction", out var system) ? Parts(system) : "Follow the task and its requested output format exactly.";
        if (payload.TryGetProperty("generationConfig", out var config) && config.TryGetProperty("responseSchema", out var schema))
            instructions += " Return only valid JSON matching this response schema (schema type names are case insensitive): " + schema.GetRawText();
        return (prompt, instructions);
    }

    public static string? ReasoningFor(AiModelSelection selection, string model, bool search = false) => search
        ? model == selection.SearchModel ? selection.SearchReasoning : model == selection.FallbackSearchModel ? selection.FallbackSearchReasoning : null
        : model == selection.TextModel ? selection.TextReasoning : model == selection.FallbackTextModel ? selection.FallbackTextReasoning : null;
}

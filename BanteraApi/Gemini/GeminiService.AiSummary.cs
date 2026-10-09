using System.Text;
using System.Text.Json;
using BanteraApi.Chat.Ai;

namespace BanteraApi.Gemini;

public partial class GeminiService
{
    public async Task<string> SummarizeAiConversationAsync(AiConversationSummary.Request request, CancellationToken ct)
    {
        var models = await modelSettings.GetAsync(ct);
        return await WithModelFallbackAsync("conversation summary", models.TextModel, models.FallbackTextModel, async (model, key) => {
            var body = new {
                systemInstruction = new { parts = new[] { new { text = AiConversationSummary.Instructions } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = JsonSerializer.Serialize(request, JsonOpts) } } } }
            };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(20));
            try {
                using var response = await SendTextRequestAsync(model, key, body, attempt.Token, reasoning: ReasoningFor(models, model));
                // Do not pass provider bodies (possibly containing conversation text) to shared logs.
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("Summary provider request failed.", null, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(attempt.Token));
                var candidate = document.RootElement.GetProperty("candidates")[0];
                if (candidate.GetProperty("finishReason").GetString() != "STOP") throw new InvalidDataException();
                var summary = string.Concat(candidate.GetProperty("content").GetProperty("parts").EnumerateArray()
                    .Where(p => !p.TryGetProperty("thought", out var thought) || thought.ValueKind != JsonValueKind.True)
                    .Select(p => p.TryGetProperty("text", out var text) ? text.GetString() : null)).Trim();
                if (summary.Length is 0 or > 6000) throw new InvalidDataException();
                return summary;
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Summary provider timed out."); }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex) { throw new HttpRequestException("Summary provider unavailable.", null, ex.StatusCode); }
            catch { throw new InvalidDataException("Summary provider returned invalid content."); }
        }, ct);
    }
}

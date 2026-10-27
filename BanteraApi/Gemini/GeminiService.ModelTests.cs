using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BanteraApi.Gemini;

public sealed record ModelTestPassage(string Maori, string English, int WordCount, string ActualTextModel,
    string ConfiguredTextModel, string? FallbackTextModel);
public sealed class ModelTestWordCountException() : Exception("The generated passage did not match the requested word count.");

public partial class GeminiService
{
    private readonly AsyncLocal<bool> privateModelEvaluation = new();
    public static int ModelTestWordCount(string text) => Regex.Matches(text.Normalize(NormalizationForm.FormC), @"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*").Count;
    public static bool ValidModelTestPassage(string? maori, string? english, int words) =>
        words is >= 5 and <= 60 && !string.IsNullOrWhiteSpace(maori) && maori.Length <= 1000 &&
        !string.IsNullOrWhiteSpace(english) && english.Length <= 2000 &&
        !maori.Contains("<2", StringComparison.Ordinal) && ModelTestWordCount(maori) == words;

    public async Task<ModelTestPassage> GenerateModelTestPassageAsync(int wordCount, CancellationToken ct)
    {
        if (wordCount is < 5 or > 60) throw new ArgumentOutOfRangeException(nameof(wordCount));
        var models = await modelSettings.GetAsync(ct);
        int? previousCount = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var value = await WithModelFallbackAsync("model test text", models.TextModel, models.FallbackTextModel, async (model, key) =>
            {
                var body = new {
                    generationConfig = new { responseMimeType = "application/json" },
                    systemInstruction = new { parts = new[] { new { text =
                        "Write a short natural passage in te reo Māori (New Zealand) and its faithful New Zealand English translation. " +
                        "Use correct macrons, everyday adult topics, complete sentences, one narrator and no speaker labels. " +
                        "The Māori passage must have EXACTLY the requested number of words. Count each whitespace-separated word once, ignoring punctuation. " +
                        "Avoid numerals, hyphenated words, quotations, invented facts and English words in the Māori passage. " +
                        "Count and revise before answering. Return JSON only: {\"maori\":\"...\",\"english\":\"...\"}. " +
                        "The English translation must correspond exactly to the final Māori passage; its length is unrestricted." } } },
                    contents = new[] { new { role = "user", parts = new[] { new { text = JsonSerializer.Serialize(new { wordCount, previousCount, instruction = previousCount is null ? "Create the passage." : "The previous attempt had the wrong word count. Write a new passage with the exact requested count." }) } } } }
                };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                using var response = await SendTextRequestAsync(model, key, body, timeout.Token, reasoning: ReasoningFor(models, model));
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("Test text generation was unavailable.", null, response.StatusCode);
                try
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                    var json = string.Concat(doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts").EnumerateArray()
                        .Where(p => !p.TryGetProperty("thought", out var thought) || thought.ValueKind != JsonValueKind.True)
                        .Select(p => p.TryGetProperty("text", out var text) ? text.GetString() : null));
                    using var passage = JsonDocument.Parse(json);
                    return (Maori: passage.RootElement.GetProperty("maori").GetString() ?? "", English: passage.RootElement.GetProperty("english").GetString() ?? "", Model: model);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                { throw new InvalidDataException("Invalid generated passage format."); }
            }, ct);
            previousCount = ModelTestWordCount(value.Maori);
            if (ValidModelTestPassage(value.Maori, value.English, wordCount))
                return new(value.Maori.Trim(), value.English.Trim(), wordCount, value.Model, models.TextModel, models.FallbackTextModel);
        }
        throw new ModelTestWordCountException();
    }

    public async Task<GeneratedAudio> GenerateModelTestAudioAsync(string maori, CancellationToken ct)
    {
        var previous = privateModelEvaluation.Value;
        privateModelEvaluation.Value = true;
        try { return await GeneratePracticeAudioAsync(maori, "mi-NZ", reply: false, level: 2, ct); }
        finally { privateModelEvaluation.Value = previous; }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace BanteraApi.Gemini;

public class GeminiService(IHttpClientFactory httpClientFactory, IOptions<GeminiSettings> options)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, string> AccentInstructions = new()
    {
        ["en-US"] = "Write the dialogue in English with a US American accent — use vocabulary and idioms typical of the United States.",
        ["en-UK"] = "Write the dialogue in English with a British accent — use vocabulary and idioms typical of the United Kingdom.",
        ["en-NZ"] = "Write the dialogue in English with a New Zealand accent — use vocabulary and idioms typical of New Zealand.",
        ["en-AU"] = "Write the dialogue in English with an Australian accent — use vocabulary and idioms typical of Australia.",
        ["en-CA"] = "Write the dialogue in English with a Canadian accent — use vocabulary and idioms typical of Canada.",
        ["en-IE"] = "Write the dialogue in English with an Irish accent — use vocabulary and idioms typical of Ireland.",
        ["zh"]    = "Write the dialogue entirely in Mandarin Chinese (简体中文). Keep it natural and conversational.",
        ["ja"]    = "Write the dialogue entirely in Japanese (日本語). Keep it natural and conversational.",
        ["ko"]    = "Write the dialogue entirely in Korean (한국어). Keep it natural and conversational.",
        ["fr"]    = "Write the dialogue entirely in French (Français). Keep it natural and conversational.",
        ["de"]    = "Write the dialogue entirely in German (Deutsch). Keep it natural and conversational.",
        ["es"]    = "Write the dialogue entirely in Spanish (Español). Keep it natural and conversational.",
        ["pt"]    = "Write the dialogue entirely in Portuguese (Português). Keep it natural and conversational.",
        ["hi"]    = "Write the dialogue entirely in Hindi (हिन्दी). Keep it natural and conversational.",
        ["ar"]    = "Write the dialogue entirely in Arabic (العربية). Keep it natural and conversational.",
        ["it"]    = "Write the dialogue entirely in Italian (Italiano). Keep it natural and conversational.",
        ["si"]    = "Write the dialogue entirely in Sinhala (සිංහල). Keep it natural and conversational.",
    };

    private static readonly HashSet<string> ValidVoiceNames =
    [
        "Kore", "Puck", "Aoede", "Charon", "Fenrir", "Leda", "Orus", "Zephyr",
        "Callirrhoe", "Autonoe", "Enceladus", "Iapetus", "Umbriel", "Algieba",
        "Despina", "Erinome", "Algenib", "Rasalgethi", "Laomedeia", "Achernar",
        "Alnilam", "Schedar", "Gacrux", "Pulcherrima", "Achird", "Zubenelgenubi",
        "Vindemiatrix", "Sadachbia", "Sadaltager", "Sulafat",
    ];

    private GeminiSettings Settings => options.Value;

    public async Task<GeneratedDialogue> GenerateDialogueAsync(
        string languageCode,
        string scenario,
        int durationSeconds,
        CancellationToken cancellationToken = default)
    {
        var accentInstruction = AccentInstructions.GetValueOrDefault(languageCode,
            $"Write the dialogue naturally in the appropriate language for locale '{languageCode}'.");

        var targetWords = (int)((durationSeconds / 60.0) * 130);
        var durationLabel = durationSeconds < 60
            ? $"{durationSeconds} seconds"
            : durationSeconds == 60 ? "1 minute"
            : $"{durationSeconds / 60} minutes";

        var scenarioLine = string.IsNullOrWhiteSpace(scenario)
            ? "Choose a random, interesting everyday scenario (e.g. ordering coffee, catching up after a holiday, a job interview, grocery shopping, getting lost on holiday)."
            : $"The scenario is: {scenario}";

        var voiceList = string.Join(" | ", ValidVoiceNames);

        var prompt = $$"""
You are a dialogue writer for conversational language learning.

{{accentInstruction}}
{{scenarioLine}}

Target audio duration: approximately {{durationLabel}}.
Write enough lines so that when spoken naturally (~130 words per minute), the dialogue fills roughly that time.
Aim for approximately {{targetWords}} words total across all speakers.

Generate a natural, realistic spoken dialogue between exactly TWO people.
- Name them Speaker1 and Speaker2.
- Alternate turns naturally; each turn should be 1–3 sentences.
- Keep sentences short and conversational — the way people actually talk.
- Do NOT include stage directions or any text outside the dialogue.
- Also write a short, catchy title for this dialogue (max 8 words).

Choose a voice for each speaker from the following list. Pick voices that suit each character's likely personality, age, and role in the scenario. Prefer different genders unless the scenario clearly involves two people of the same gender. Use the exact name as shown.
Available voices: {{voiceList}}

Return ONLY valid JSON in this exact format, no markdown fences, no extra keys:
{
  "title": "...",
  "voice1": "VoiceNameHere",
  "voice2": "VoiceNameHere",
  "lines": [
    { "speaker": "Speaker1", "text": "..." },
    { "speaker": "Speaker2", "text": "..." }
  ]
}
""".Trim();

        return await WithGeminiKeyAsync(async key =>
        {
            var client = httpClientFactory.CreateClient("gemini");
            var url = $"/v1beta/models/{Settings.TextModel}:generateContent?key={key}";
            var body = new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } }
            };

            using var response = await client.PostAsync(
                url,
                new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
                cancellationToken);

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonDocument.Parse(json).RootElement;
            var raw = root
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? "";

            var cleaned = raw
                .Replace("```json", "").Replace("```", "")
                .Trim();

            RawDialogue? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<RawDialogue>(cleaned, JsonOpts);
            }
            catch
            {
                var start = cleaned.IndexOf('{');
                var end = cleaned.LastIndexOf('}');
                if (start < 0 || end < 0) throw new InvalidOperationException("Could not parse dialogue JSON from Gemini response.");
                parsed = JsonSerializer.Deserialize<RawDialogue>(cleaned[start..(end + 1)], JsonOpts);
            }

            if (parsed is null) throw new InvalidOperationException("Gemini returned null dialogue.");

            var voice1 = ValidVoiceNames.Contains(parsed.Voice1 ?? "") ? parsed.Voice1! : "Kore";
            var voice2 = ValidVoiceNames.Contains(parsed.Voice2 ?? "") ? parsed.Voice2! : "Puck";

            return new GeneratedDialogue(
                Title: parsed.Title ?? "Dialogue",
                Voice1: voice1,
                Voice2: voice2,
                Lines: (parsed.Lines ?? [])
                    .Select(l => new DialogueLine(l.Speaker ?? "Speaker1", l.Text ?? ""))
                    .ToArray());
        }, cancellationToken);
    }

    public async Task<(byte[] WavBytes, int DurationMs)> GenerateAudioAsync(
        GeneratedDialogue dialogue,
        CancellationToken cancellationToken = default)
    {
        var transcript = string.Join("\n",
            dialogue.Lines.Select(l => $"{l.Speaker}: {l.Text}"));

        return await WithGeminiKeyAsync(async key =>
        {
            var client = httpClientFactory.CreateClient("gemini");
            var url = $"/v1beta/models/{Settings.AudioModel}:generateContent?key={key}";
            var body = new
            {
                contents = new[] { new { parts = new[] { new { text = transcript } } } },
                generationConfig = new
                {
                    responseModalities = new[] { "AUDIO" },
                    speechConfig = new
                    {
                        multiSpeakerVoiceConfig = new
                        {
                            speakerVoiceConfigs = new[]
                            {
                                new { speaker = "Speaker1", voiceConfig = new { prebuiltVoiceConfig = new { voiceName = dialogue.Voice1 } } },
                                new { speaker = "Speaker2", voiceConfig = new { prebuiltVoiceConfig = new { voiceName = dialogue.Voice2 } } },
                            }
                        }
                    }
                }
            };

            using var response = await client.PostAsync(
                url,
                new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
                cancellationToken);

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonDocument.Parse(json).RootElement;
            var inlineData = root
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("inlineData");

            var base64Data = inlineData.GetProperty("data").GetString()
                ?? throw new InvalidOperationException("No audio data in Gemini response.");
            var mimeType = inlineData.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "" : "";

            var pcm = Convert.FromBase64String(base64Data);

            int sampleRate = 24000;
            var rateMatch = System.Text.RegularExpressions.Regex.Match(mimeType, @"rate=(\d+)");
            if (rateMatch.Success) sampleRate = int.Parse(rateMatch.Groups[1].Value);

            byte[] wavBytes;
            if (mimeType.Contains("L16") || mimeType.Contains("pcm"))
            {
                wavBytes = PcmToWav(pcm, sampleRate);
            }
            else
            {
                wavBytes = pcm;
            }

            var durationMs = (int)((double)pcm.Length / (sampleRate * 1 * 2) * 1000);
            return (wavBytes, durationMs);
        }, cancellationToken);
    }

    public IReadOnlyList<VideoTranscriptCueRecord> EstimateCues(DialogueLine[] lines, int durationMs)
    {
        if (lines.Length == 0) return [];

        var totalWords = lines.Sum(l => CountWords(l.Text));
        if (totalWords == 0) totalWords = 1;

        var cues = new List<VideoTranscriptCueRecord>(lines.Length);
        var cursor = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var words = CountWords(lines[i].Text);
            var end = i == lines.Length - 1
                ? durationMs
                : cursor + (int)((double)words / totalWords * durationMs);
            cues.Add(new VideoTranscriptCueRecord(i, cursor, end, lines[i].Text));
            cursor = end;
        }
        return cues;
    }

    private static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static byte[] PcmToWav(byte[] pcm, int sampleRate = 24000, int channels = 1, int bitsPerSample = 16)
    {
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8.ToArray()); bw.Write(36 + pcm.Length);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray()); bw.Write(16);
        bw.Write((short)1); bw.Write((short)channels);
        bw.Write(sampleRate); bw.Write(byteRate);
        bw.Write(blockAlign); bw.Write((short)bitsPerSample);
        bw.Write("data"u8.ToArray()); bw.Write(pcm.Length);
        bw.Write(pcm);
        return ms.ToArray();
    }

    private string[] GetShuffledKeys()
    {
        var keys = Settings.ApiKeys.ToArray();
        for (var i = keys.Length - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (keys[i], keys[j]) = (keys[j], keys[i]);
        }
        return keys;
    }

    private async Task<T> WithGeminiKeyAsync<T>(Func<string, Task<T>> fn, CancellationToken cancellationToken)
    {
        var keys = GetShuffledKeys();
        if (keys.Length == 0) throw new InvalidOperationException("No Gemini API keys configured.");

        Exception? lastEx = null;
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await fn(key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastEx = ex;
            }
        }
        throw new InvalidOperationException("All Gemini API keys failed.", lastEx);
    }

    // ── Internal deserialization types ───────────────────────────────

    private record RawDialogue(
        [property: JsonPropertyName("title")]  string? Title,
        [property: JsonPropertyName("voice1")] string? Voice1,
        [property: JsonPropertyName("voice2")] string? Voice2,
        [property: JsonPropertyName("lines")]  RawLine[]? Lines);

    private record RawLine(
        [property: JsonPropertyName("speaker")] string? Speaker,
        [property: JsonPropertyName("text")]    string? Text);
}

// ── Public result types ───────────────────────────────────────────────

public record GeneratedDialogue(string Title, string Voice1, string Voice2, DialogueLine[] Lines);

public record DialogueLine(string Speaker, string Text);

public record VideoTranscriptCueRecord(int Index, int StartMs, int EndMs, string Text);

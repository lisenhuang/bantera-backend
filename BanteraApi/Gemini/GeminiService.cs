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
        ["en-US"] = "Write the dialogue STRICTLY in US American English. The speakers MUST sound authentically American — use American vocabulary, spellings, and idioms (e.g. 'gotten', 'sidewalk', 'faucet', 'trash can', 'apartment'). Do NOT use British, Australian, or any other English variant.",
        ["en-GB"] = "Write the dialogue STRICTLY in British English. The speakers MUST sound authentically British — use British vocabulary, spellings, and idioms (e.g. 'brilliant', 'cheers', 'biscuit', 'flat', 'rubbish', 'queue'). Do NOT use American or Australian English.",
        ["en-UK"] = "Write the dialogue STRICTLY in British English. The speakers MUST sound authentically British — use British vocabulary, spellings, and idioms (e.g. 'brilliant', 'cheers', 'biscuit', 'flat', 'rubbish', 'queue'). Do NOT use American or Australian English.",
        ["en-NZ"] = "Write the dialogue STRICTLY in New Zealand English. The speakers MUST sound authentically Kiwi — use New Zealand vocabulary and idioms (e.g. 'sweet as', 'chur', 'bach', 'dairy', 'togs', 'jandals', 'kia ora', 'choice'). This is NOT Australian English. Do NOT use Australian slang or idioms.",
        ["en-AU"] = "Write the dialogue STRICTLY in Australian English. The speakers MUST sound authentically Australian — use Australian vocabulary and idioms (e.g. 'arvo', 'brekkie', 'no worries', 'mate', 'servo', 'bottle-o'). Do NOT use New Zealand, British, or American English.",
        ["en-CA"] = "Write the dialogue STRICTLY in Canadian English. The speakers MUST sound authentically Canadian — use Canadian vocabulary and idioms (e.g. 'toque', 'loonie', 'double-double', 'eh', 'hydro'). Do NOT use American or British English.",
        ["en-IE"] = "Write the dialogue STRICTLY in Irish English. The speakers MUST sound authentically Irish — use Irish vocabulary and idioms (e.g. 'grand', 'craic', 'gas', 'your man', 'deadly'). Do NOT use British or American English.",
        ["en-IN"] = "Write the dialogue STRICTLY in Indian English. The speakers MUST sound authentically Indian — use Indian English vocabulary, phrasing and idioms natural to India (e.g. 'prepone', 'do the needful', 'out of station', 'revert back', 'itself'). Sentence rhythm should reflect Indian English patterns. Do NOT use British, American, or Australian English.",
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

    // Accent instructions injected into the TTS content so Gemini speaks the right accent.
    private static readonly Dictionary<string, string> TtsAccentInstructions = new()
    {
        ["en-US"] = "Both speakers must use a natural, authentic US American accent throughout. Do not use Australian, British, or New Zealand accents.",
        ["en-GB"] = "Both speakers must use a natural, authentic British English accent throughout. Do not use Australian, American, or New Zealand accents.",
        ["en-UK"] = "Both speakers must use a natural, authentic British English accent throughout. Do not use Australian, American, or New Zealand accents.",
        ["en-NZ"] = "Both speakers must use a natural, authentic New Zealand English (Kiwi) accent throughout. This is NOT an Australian accent — New Zealand English has a distinctly higher, flatter vowel sound. Do not use Australian accents.",
        ["en-AU"] = "Both speakers must use a natural, authentic Australian accent throughout. Do not use New Zealand, British, or American accents.",
        ["en-CA"] = "Both speakers must use a natural, authentic Canadian English accent throughout.",
        ["en-IE"] = "Both speakers must use a natural, authentic Irish English accent throughout.",
        ["en-IN"] = "Both speakers must use a natural, authentic Indian English accent throughout — the distinctive rhythm, intonation and stress patterns of Indian English speakers. Do not use British, American, or Australian accents.",
    };

    // Voice library — each voice tagged by gender and style keywords.
    // The text model describes character style; backend scores and picks the best match.
    private record VoiceProfile(string Name, string Gender, string[] Styles);

    private static readonly VoiceProfile[] VoiceLibrary =
    [
        // Female voices
        new("Kore",         "female", ["warm", "gentle"]),
        new("Aoede",        "female", ["friendly", "bright", "youthful"]),
        new("Leda",         "female", ["warm", "conversational"]),
        new("Callirrhoe",   "female", ["gentle", "sincere"]),
        new("Autonoe",      "female", ["energetic", "playful", "expressive"]),
        new("Despina",      "female", ["cheerful", "youthful", "playful"]),
        new("Erinome",      "female", ["calm", "gentle", "refined"]),
        new("Laomedeia",    "female", ["professional", "authoritative", "articulate"]),
        new("Pulcherrima",  "female", ["playful", "charming", "expressive"]),
        new("Vindemiatrix", "female", ["professional", "authoritative", "crisp"]),
        new("Sulafat",      "female", ["warm", "expressive", "melodic"]),
        // Male voices
        new("Puck",          "male", ["playful", "witty", "youthful"]),
        new("Charon",        "male", ["authoritative", "calm", "mature"]),
        new("Fenrir",        "male", ["authoritative", "intense", "confident"]),
        new("Orus",          "male", ["professional", "steady", "calm"]),
        new("Zephyr",        "male", ["playful", "friendly", "youthful"]),
        new("Enceladus",     "male", ["authoritative", "serious", "mature"]),
        new("Iapetus",       "male", ["professional", "neutral", "calm"]),
        new("Umbriel",       "male", ["gentle", "calm", "reflective"]),
        new("Algieba",       "male", ["confident", "expressive", "smooth"]),
        new("Algenib",       "male", ["energetic", "crisp", "youthful"]),
        new("Rasalgethi",    "male", ["warm", "mature", "sincere"]),
        new("Achernar",      "male", ["youthful", "energetic", "playful"]),
        new("Alnilam",       "male", ["professional", "neutral", "steady"]),
        new("Schedar",       "male", ["warm", "friendly", "sincere"]),
        new("Gacrux",        "male", ["authoritative", "commanding", "mature"]),
        new("Achird",        "male", ["friendly", "warm", "casual"]),
        new("Zubenelgenubi", "male", ["calm", "gentle", "thoughtful"]),
        new("Sadachbia",     "male", ["professional", "composed", "neutral"]),
        new("Sadaltager",    "male", ["expressive", "smooth", "confident"]),
    ];

    // Pick the voice that best matches gender + style. Excludes `exclude` to avoid duplicates.
    private static string PickVoice(string gender, string[] styles, string? exclude = null)
    {
        var candidates = VoiceLibrary
            .Where(v => v.Gender == gender && v.Name != exclude)
            .ToArray();

        // Fall back to opposite gender if the pool is somehow empty.
        if (candidates.Length == 0)
            candidates = VoiceLibrary.Where(v => v.Name != exclude).ToArray();

        // Score by how many requested styles each voice has, with a random tiebreak.
        return candidates
            .Select(v => (v.Name, Score: styles.Count(s => v.Styles.Contains(s, StringComparer.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.Score)
            .ThenBy(_ => Random.Shared.Next())
            .First().Name;
    }

    private static (string Voice1, string Voice2) PickVoices(string gender1, string[] styles1, string gender2, string[] styles2)
    {
        var v1 = PickVoice(gender1, styles1);
        var v2 = PickVoice(gender2, styles2, exclude: v1);
        return (v1, v2);
    }

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

        var prompt = $$"""
You are a dialogue writer for conversational language learning.

CONTENT POLICY (follow strictly):
- Output is for neutral, everyday language practice only.
- Do NOT generate dialogue, titles, or scenarios about politics of the People's Republic of China, its government or ruling party, or political leadership past or present; do NOT include politically sensitive topics concerning China.
- Do NOT generate content about any country's government, political leaders, elections, or politically sensitive current events when those would dominate the scene.
- If the user's scenario OR any honest interpretation of it would require violating the above, you MUST refuse by returning ONLY this exact JSON (no markdown, no other text):
{"rejected":true}
- If you accept the scenario, the title and every line must stay fully clear of those topics.

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

For each speaker, determine:
  • gender — RULES: if the scenario explicitly states a character's gender (e.g. "girl", "boy", "woman", "man", "he", "she"), you MUST use that gender. Otherwise infer from context. Output only "male" or "female".
  • styles — choose 1–3 that best describe the character's personality and the emotional tone they bring to the scene. Pick ONLY from this list: warm, friendly, playful, youthful, energetic, gentle, calm, sincere, expressive, professional, authoritative, confident, mature, intense, smooth, casual

Return ONLY valid JSON in this exact format, no markdown fences, no extra keys:
{
  "title": "...",
  "speaker1_gender": "female",
  "speaker1_styles": ["warm", "playful"],
  "speaker2_gender": "male",
  "speaker2_styles": ["authoritative", "mature"],
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

            // Check if Gemini refused the scenario.
            if (cleaned.Contains("\"rejected\"", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var rejDoc = JsonDocument.Parse(cleaned);
                    if (rejDoc.RootElement.TryGetProperty("rejected", out var rVal) && rVal.GetBoolean())
                        throw new ContentRejectedException("This topic cannot be used for generation. Please choose a different scenario.");
                }
                catch (ContentRejectedException) { throw; }
                catch { /* not a rejection payload, continue normal parsing */ }
            }

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

            var gender1 = parsed.Speaker1Gender?.Trim().ToLowerInvariant() == "female" ? "female" : "male";
            var gender2 = parsed.Speaker2Gender?.Trim().ToLowerInvariant() == "female" ? "female" : "male";
            var styles1 = parsed.Speaker1Styles ?? [];
            string[] styles2 = parsed.Speaker2Styles ?? [];
            var (voice1, voice2) = PickVoices(gender1, styles1, gender2, styles2);

            var dialogueLines = (parsed.Lines ?? [])
                .Select(l => new DialogueLine(l.Speaker ?? "Speaker1", l.Text ?? ""))
                .ToArray();

            return new GeneratedDialogue(
                Title: parsed.Title ?? "Dialogue",
                Voice1: voice1,
                Voice2: voice2,
                Lines: dialogueLines);
        }, cancellationToken);
    }

    public async Task<(byte[] WavBytes, int DurationMs)> GenerateAudioAsync(
        GeneratedDialogue dialogue,
        string languageCode,
        CancellationToken cancellationToken = default)
    {
        var ttsAccent = TtsAccentInstructions.GetValueOrDefault(languageCode);
        var dialogueText = string.Join("\n",
            dialogue.Lines.Select(l => $"{l.Speaker}: {l.Text}"));
        var transcript = ttsAccent != null
            ? $"[Accent instruction: {ttsAccent}]\n\n{dialogueText}"
            : dialogueText;

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

    /// Corrects the text of phone-transcribed cues using the original dialogue as
    /// ground truth. Preserves every cue's start/end timestamps exactly.
    public async Task<IReadOnlyList<VideoTranscriptCueRecord>> CorrectTranscriptAsync(
        string[] originalLines,
        IReadOnlyList<VideoTranscriptCueRecord> transcribedCues,
        CancellationToken cancellationToken = default)
    {
        if (transcribedCues.Count == 0) return transcribedCues;

        var originalScript = string.Join("\n", originalLines.Select((l, i) => $"{i + 1}. {l}"));
        var cueList = string.Join("\n", transcribedCues.Select((c, i) => $"{i + 1}. {c.Text}"));

        var prompt = $"""
You are a transcript corrector.

Below is the ORIGINAL script (ground truth) for an AI-generated audio dialogue:
{originalScript}

Below is a phone-transcribed version of the same audio, split into {transcribedCues.Count} timed cues.
The transcription may contain errors (wrong words, missing words, phonetic mistakes) because speech recognition is imperfect.

{cueList}

Your task:
- Correct the text of each cue to match the original script as closely as possible.
- The cue boundaries and count must stay EXACTLY the same — do not merge or split cues.
- Each corrected cue should contain the portion of the original script that best matches that time segment.
- Return ONLY a JSON array of corrected strings, one per cue, in the same order. No extra keys, no markdown fences.

Example output for 3 cues:
["corrected text 1","corrected text 2","corrected text 3"]
""".Trim();

        return await WithGeminiKeyAsync(async key =>
        {
            var client = httpClientFactory.CreateClient("gemini");
            var url = $"/v1beta/models/{Settings.TextModel}:generateContent?key={key}";
            var body = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };

            using var response = await client.PostAsync(
                url,
                new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
                cancellationToken);

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonDocument.Parse(json).RootElement;
            var raw = root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            var cleaned = raw.Replace("```json", "").Replace("```", "").Trim();

            string[]? corrected;
            try { corrected = JsonSerializer.Deserialize<string[]>(cleaned, JsonOpts); }
            catch
            {
                var s = cleaned.IndexOf('['); var e = cleaned.LastIndexOf(']');
                corrected = s >= 0 && e > s ? JsonSerializer.Deserialize<string[]>(cleaned[s..(e + 1)], JsonOpts) : null;
            }

            if (corrected == null || corrected.Length != transcribedCues.Count)
                return transcribedCues; // fallback: return original transcribed cues unchanged

            return transcribedCues.Select((c, i) => c with { Text = corrected[i].Trim() }).ToList();
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
        [property: JsonPropertyName("title")]           string?   Title,
        [property: JsonPropertyName("speaker1_gender")] string?   Speaker1Gender,
        [property: JsonPropertyName("speaker1_styles")] string[]? Speaker1Styles,
        [property: JsonPropertyName("speaker2_gender")] string?   Speaker2Gender,
        [property: JsonPropertyName("speaker2_styles")] string[]? Speaker2Styles,
        [property: JsonPropertyName("lines")]           RawLine[]? Lines);

    private record RawLine(
        [property: JsonPropertyName("speaker")] string? Speaker,
        [property: JsonPropertyName("text")]    string? Text);
}

// ── Public result types ───────────────────────────────────────────────

public record GeneratedDialogue(string Title, string Voice1, string Voice2, DialogueLine[] Lines);

public record DialogueLine(string Speaker, string Text);

public record VideoTranscriptCueRecord(int Index, int StartMs, int EndMs, string Text);

public class ContentRejectedException(string message) : Exception(message);

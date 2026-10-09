using System.Net;
using System.Text;
using System.Text.Json;
using BanteraApi.Admin;
using BanteraApi.Gemini;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class GeminiServicePromptTests
{
    // Expected provider hints audited against Google's language table on 2026-09-29.
    public static TheoryData<string, string?> TranscriptionLocales => new()
    {
        { "en-US", "en-US" },
        { "en-GB", "en-GB" },
        { "en-AU", null },
        { "en-CA", null },
        { "en-IN", "en-IN" },
        { "en-NZ", null },
        { "en-IE", null },
        { "en-SG", null },
        { "en-ZA", null },
        { "en-PH", null },
        { "en-AE", null },
        { "en-ID", null },
        { "en-SA", null },
        { "es-MX", null },
        { "es-ES", null },
        { "es-419", "es-419" },
        { "es-US", "es-US" },
        { "es-CO", null },
        { "es-CL", null },
        { "fr-FR", "fr-FR" },
        { "fr-CA", null },
        { "fr-BE", null },
        { "fr-CH", null },
        { "de-DE", "de-DE" },
        { "de-AT", null },
        { "de-CH", null },
        { "it-IT", "it-IT" },
        { "it-CH", null },
        { "zh-CN", "cmn-Hans-CN" },
        { "zh-TW", null },
        { "zh-HK", "yue-Hant-HK" },
        { "yue-CN", "yue-Hant-HK" },
        { "ja-JP", "ja-JP" },
        { "ko-KR", "ko-KR" },
        { "pt-BR", "pt-BR" },
        { "pt-PT", "pt-PT" },
        { "ar-SA", null },
        { "ar-AE", null },
        { "ru-RU", "ru-RU" },
        { "hi-IN", "hi-IN" },
        { "id-ID", "id-ID" },
        { "vi-VN", "vi-VN" },
        { "th-TH", "th-TH" },
        { "tr-TR", "tr-TR" },
        { "nl-NL", "nl-NL" },
        { "nl-BE", null },
        { "pl-PL", "pl-PL" },
        { "sv-SE", "sv-SE" },
        { "da-DK", "da-DK" },
        { "nb-NO", "nb-NO" },
        { "fi-FI", "fi-FI" },
        { "uk-UA", "uk-UA" },
        { "el-GR", "el-GR" },
        { "cs-CZ", "cs-CZ" },
        { "sk-SK", "sk-SK" },
        { "hu-HU", "hu-HU" },
        { "ro-RO", "ro-RO" },
        { "hr-HR", "hr-HR" },
        { "he-IL", "he-IL" },
        { "ms-MY", "ms-MY" },
        { "ca-ES", "ca-ES" },
    };

    [Theory]
    [MemberData(nameof(TranscriptionLocales))]
    public async Task EveryLearningLocaleSendsADocumentedHintOrAutomaticDetection(string locale, string? expectedHint)
    {
        var handler = new TranscriptionHandler();
        var words = await CreateService(handler).TranscribeWordsAsync([1, 2, 3], "audio/mpeg", locale);
        using var document = JsonDocument.Parse(Assert.Single(handler.Requests));
        var root = document.RootElement;
        Assert.Equal("gemini-3.5-transcribe", root.GetProperty("model").GetString());
        var config = root.GetProperty("generation_config").GetProperty("transcription_config");
        var hints = config.GetProperty("language_codes").EnumerateArray().Select(h => h.GetString()).ToArray();
        Assert.Equal(expectedHint is null ? [] : new[] { expectedHint }, hints);
        var mode = config.GetProperty("mode");
        Assert.Equal("verbatim", mode.GetProperty("type").GetString());
        Assert.Equal("speaker", mode.GetProperty("diarization_mode").GetString());
        Assert.Equal("word", mode.GetProperty("timestamp_granularities")[0].GetString());
        var input = Assert.Single(root.GetProperty("input").EnumerateArray());
        Assert.Equal("audio", input.GetProperty("type").GetString());
        Assert.Equal("AQID", input.GetProperty("data").GetString());
        Assert.Equal("咗", Assert.Single(words).Text);
    }

    [Fact]
    public void TranscriptionAuditCoversTheEntireLearningCatalog()
        => Assert.Equal(LearningLanguageCatalog.Items.Select(x => x.Identifier).Order(),
            TranscriptionLocales.Select(row => (string)row[0]).Order());

    [Theory]
    [InlineData(" zh-hk ", "yue-Hant-HK")]
    [InlineData("YUE-CN", "yue-Hant-HK")]
    [InlineData("yue-hant-hk", "yue-Hant-HK")]
    [InlineData("CMN-HANS-CN", "cmn-Hans-CN")]
    [InlineData("EN-us", "en-US")]
    [InlineData("unknown", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TranscriptionHintHandlesStoredCasingAndUnknownLocales(string? input, string? expected)
        => Assert.Equal(expected, GeminiTranscriptionLanguages.Resolve(input));

    [Fact]
    public async Task ProviderLanguageRejectionRetainsAutomaticDetectionFallback()
    {
        var handler = new TranscriptionHandler(rejectHint: true);
        var words = await CreateService(handler).TranscribeWordsAsync([1], "audio/mpeg", "zh-HK");
        Assert.Equal(2, handler.Requests.Count);
        using var first = JsonDocument.Parse(handler.Requests[0]);
        using var second = JsonDocument.Parse(handler.Requests[1]);
        Assert.Equal("yue-Hant-HK", first.RootElement.GetProperty("generation_config")
            .GetProperty("transcription_config").GetProperty("language_codes")[0].GetString());
        Assert.Empty(second.RootElement.GetProperty("generation_config")
            .GetProperty("transcription_config").GetProperty("language_codes").EnumerateArray());
        Assert.Single(words);
    }

    private sealed class TranscriptionHandler(bool rejectHint = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v1beta/interactions", request.RequestUri!.AbsolutePath);
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (rejectHint && Requests.Count == 1)
                return new(HttpStatusCode.BadRequest) { Content = new StringContent("unsupported language") };
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"status":"completed","steps":[{"type":"model_output","content":[{"annotations":[
                        {"type":"word_info","text":"咗","start_offset":"19.900s","end_offset":"20.100s","speaker":"spk:1"}
                    ]}]}]}
                    """, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Theory]
    [InlineData("beginner", "Difficulty: Beginner", "Use basic, simple words that a beginner can easily understand")]
    [InlineData("intermediate", "Difficulty: Intermediate", "accessible sentences")]
    [InlineData("advanced", "Difficulty: Advanced", "nuanced opinions")]
    public async Task DialogueLevelChangesPromptAndSurvivesIntoSpeech(string level, string heading, string instruction)
    {
        var handler = new CapturingHandler();
        var dialogue = await CreateService(handler).GenerateDialogueAsync("English", "en-NZ", "coffee", 60, level: level);
        Assert.Equal(level, dialogue.Level);
        Assert.Contains(heading, handler.GetPrompt());
        Assert.Contains(instruction, handler.GetPrompt());
        Assert.Contains("simplify within the selected spoken language and regional variety", handler.GetPrompt());
        if (level == "beginner")
        {
            Assert.Contains("Retain basic everyday colloquial words and grammatical particles", handler.GetPrompt());
            Assert.DoesNotContain("Avoid idioms, slang", handler.GetPrompt());
        }
        if (level == "advanced")
        {
            Assert.DoesNotContain("Keep sentences short", handler.GetPrompt());
            Assert.DoesNotContain("Keep the language plain", handler.GetPrompt());
        }
    }

    [Theory]
    [InlineData("zh-HK", "beginner", "spoken Hong Kong Cantonese", "Traditional Chinese")]
    [InlineData("zh-HK", "intermediate", "spoken Hong Kong Cantonese", "Traditional Chinese")]
    [InlineData("zh-HK", "advanced", "spoken Hong Kong Cantonese", "Traditional Chinese")]
    [InlineData("zh-hk", "advanced", "spoken Hong Kong Cantonese", "Traditional Chinese")]
    [InlineData("yue-CN", "beginner", "spoken Cantonese as used in Guangdong", "Chinese characters")]
    [InlineData("yue-CN", "intermediate", "spoken Cantonese as used in Guangdong", "Chinese characters")]
    [InlineData("yue-CN", "advanced", "spoken Cantonese as used in Guangdong", "Chinese characters")]
    [InlineData("yue-cn", "advanced", "spoken Cantonese as used in Guangdong", "Chinese characters")]
    public async Task CantoneseDialogueKeepsSpokenLanguageAtEveryLevel(
        string locale, string level, string regionalInstruction, string scriptInstruction)
    {
        var handler = new CapturingHandler();
        // Even an older caller's generic language label must use the locale's Cantonese rules.
        var dialogue = await CreateService(handler).GenerateDialogueAsync("Chinese", locale, "ordering food", 60, level: level);
        var prompt = handler.GetPrompt();

        Assert.Equal(level, dialogue.Level);
        Assert.Contains(regionalInstruction, prompt);
        Assert.Contains(scriptInstruction, prompt);
        Assert.Contains("Simplify within Cantonese", prompt);
        Assert.Contains("do not replace Cantonese wording or grammar with Mandarin or standard written Chinese", prompt);
        Assert.Contains("食、咩、咩嘢、唔、冇、喺、係、佢、我哋 and 咗", prompt);
        Assert.Contains("你想食咩？", prompt);
        Assert.DoesNotContain("natural Chinese for Hong Kong", prompt);
    }

    [Theory]
    [InlineData("beginner", "gemini-3.8-flash-tts", "deliberately slower")]
    [InlineData("beginner", "gemini-2.5-flash-preview-tts", "deliberately slower")]
    [InlineData("advanced", "gemini-3.8-flash-lite-tts", "full, natural conversational pace")]
    [InlineData("advanced", "gemini-3.1-flash-tts-preview", "full, natural conversational pace")]
    public async Task SpeechLevelReachesBothTtsFormats(string level, string model, string instruction)
    {
        var handler = new PreviewAudioHandler();
        var dialogue = new GeneratedDialogue("Chat", "Kore", "Puck",
            [new("Speaker1", "Hello."), new("Speaker2", "Hi.")], []) { Level = level };
        await CreateService(handler, new("text", model, null, null)).GenerateAudioAsync(dialogue, "en-US");
        using var document = JsonDocument.Parse(Assert.Single(handler.Requests));
        var parts = document.RootElement.GetProperty("contents")[0].GetProperty("parts");
        if (model.Contains("3.8"))
        {
            foreach (var part in parts.EnumerateArray())
                Assert.Contains(instruction, part.GetProperty("speech_metadata").GetProperty("style").GetString());
            Assert.Equal("Hello.", parts[0].GetProperty("text").GetString());
        }
        else
            Assert.Contains(instruction, parts[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ScriptShortCuesCombineTinyPiecesOnlyWithinTheirSpeakerLine()
    {
        var response = JsonSerializer.Serialize(new
        {
            title = "食飯", speaker1_gender = "female", speaker2_gender = "male",
            lines = new[]
            {
                new { speaker = "Speaker1", text = "哇，真係好特別呀，科技幫到文化保育真係好好。",
                    shortCues = new[] { "哇，", "真係好特別呀，", "科技幫到文化保育真係好好。" } },
                new { speaker = "Speaker2", text = "係呀。", shortCues = new[] { "係呀。" } },
            },
        });
        var handler = new CapturingHandler(responseDialogue: response);
        var dialogue = await CreateService(handler).GenerateDialogueAsync("Cantonese", "zh-HK", "chat", 60);

        Assert.Equal(["哇，真係好特別呀，", "科技幫到文化保育真係好好。"], dialogue.Lines[0].ShortCues);
        Assert.Equal(["係呀。"], dialogue.Lines[1].ShortCues);
        Assert.Equal("Speaker2", dialogue.Lines[1].Speaker);
        Assert.Equal(dialogue.Lines[0].Text, string.Concat(dialogue.Lines[0].ShortCues));
        Assert.Contains("Never combine different speakers in one cue", handler.GetPrompt());
    }

    [Fact]
    public async Task GenerateDialogueAsync_LatestNewsPrompt_AllowsFewerStoriesThanRequested()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler, apiKeys: ["AIzaSy-test-search-key"]);

        await service.GenerateDialogueAsync(
            "English",
            "en-US",
            "",
            240,
            "latest_news",
            "Japanese",
            "ja-JP");

        var prompt = handler.GetPrompt();

        Assert.Contains("Try to find up to 4 real recent news stories.", prompt);
        Assert.Contains("treat this regional mix as flexible", prompt);
        Assert.Contains("do not treat this count as a hard requirement", prompt);
        Assert.Contains("If fewer suitable real recent stories are found, go ahead", prompt);
        Assert.Contains("One suitable real recent story is enough to proceed", prompt);
        Assert.Contains("Do not invent, pad, or fabricate missing stories", prompt);
        Assert.DoesNotContain("Weave all 4", prompt);
        Assert.DoesNotContain("Aim for approximately", prompt);
        Assert.Contains("words total across all speakers", prompt);
    }

    [Fact]
    public async Task SearchWithOnlyDisallowedKeysNeverContactsGemini()
    {
        var handler = new FallbackHandler(audio: false);
        var service = CreateService(handler, apiKeys: ["AQ-not-for-search"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateDialogueAsync("English", "en-US", "", 60, "latest_news"));
        Assert.Empty(handler.Models);
    }

    [Fact]
    public async Task GenerateDialogueAsync_NonNewsPrompt_IncludesExplicitWordTarget()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);

        await service.GenerateDialogueAsync(
            "English",
            "en-US",
            "ordering coffee",
            120);

        var prompt = handler.GetPrompt();

        Assert.Contains("Target audio duration: approximately 2 minutes.", prompt);
        Assert.Contains("normal conversational pace", prompt);
        Assert.Contains("Script length target: 350 words", prompt);
        Assert.Contains("suggested range: 315-385", prompt);
        Assert.Contains("Use enough turns to fit the requested duration", prompt);
        Assert.DoesNotContain("Aim for approximately", prompt);
        Assert.Contains("words total across all speakers", prompt);
        Assert.Contains("One character must be male and the other female", prompt);
        Assert.Contains("Choose names natural to the target language and locale", prompt);
    }

    [Fact]
    public async Task GenerateDialogueAsync_RestaurantPresetDoesNotRequirePizzaForOlderApps()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);

        await service.GenerateDialogueAsync("English", "en-US",
            "Two friends argue lightheartedly about what to order at a pizza restaurant.",
            60, "restaurant_order");

        var prompt = handler.GetPrompt();
        Assert.Contains("Do not default to pizza", prompt);
        Assert.DoesNotContain("at a pizza restaurant", prompt);
    }

    [Fact]
    public async Task GenerateDialogueAsync_RetriesSameGenderResponseBeforeChoosingVoices()
    {
        var handler = new CapturingHandler([("female", "female"), ("female", "male")]);
        var service = CreateService(handler);

        var dialogue = await service.GenerateDialogueAsync("English", "en-US", "ordering coffee", 120);

        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("Regenerate the entire dialogue with opposite speaker genders", handler.GetPrompt());
        Assert.NotEqual(dialogue.Voice1, dialogue.Voice2);
    }

    [Fact]
    public async Task GenerateDialogueAsync_RejectsPersistentlySameGenderResponse()
    {
        var handler = new CapturingHandler([("male", "male")]);
        var service = CreateService(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateDialogueAsync("English", "en-US", "ordering coffee", 120));

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GenerateAudioAsync_RejectsTwoVoicesOfTheSameGender()
    {
        var service = CreateService(new CapturingHandler());
        var dialogue = new GeneratedDialogue("Coffee", "Kore", "Aoede", [], []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAudioAsync(dialogue, "en-US"));
    }

    [Theory]
    [InlineData("gemini-2.5-flash-preview-tts")]
    [InlineData("gemini-2.5-pro-preview-tts")]
    [InlineData("gemini-3.1-flash-tts-preview")]
    public async Task GenerateAudioAsync_PreviewModelSendsWholeDialogueWithBothVoices(string model)
    {
        var handler = new PreviewAudioHandler();
        var service = CreateService(handler, new AiModelSelection("text", model, null, null));
        var lines = Enumerable.Range(0, 10)
            .Select(i => new DialogueLine(i % 2 == 0 ? "Speaker1" : "Speaker2", $"Line {i}"))
            .ToArray();
        var dialogue = new GeneratedDialogue("Chat", "Kore", "Puck", lines, []);

        var audio = await service.GenerateAudioAsync(dialogue, "en-US");

        Assert.Single(handler.Requests);
        Assert.True(audio.DurationMs >= 200);
        using var document = JsonDocument.Parse(handler.Requests[0]);
        var config = document.RootElement.GetProperty("generationConfig")
            .GetProperty("speechConfig").GetProperty("multiSpeakerVoiceConfig")
            .GetProperty("speakerVoiceConfigs");
        Assert.Equal("Kore", config[0].GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
        Assert.Equal("Puck", config[1].GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
        var transcript = document.RootElement.GetProperty("contents")[0]
            .GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Contains("Speaker1: Line 0", transcript);
        Assert.Contains("Speaker2: Line 9", transcript);
        Assert.DoesNotContain("speech_metadata", handler.Requests[0]);
        if (model == "gemini-2.5-flash-preview-tts")
        {
            Assert.Contains("Speaker1 is female; Speaker2 is male", transcript);
            Assert.Contains("throughout the entire recording", transcript);
            Assert.Contains("do not say the speaker labels", transcript);
        }
        else
        {
            Assert.Contains("Speaker1 has a female voice and Speaker2 has a male voice", transcript);
            Assert.DoesNotContain("throughout the entire recording", transcript);
        }
    }

    [Theory]
    [InlineData("gemini-3.8-flash-tts", false)]
    [InlineData("gemini-3.8-flash-tts", true)]
    [InlineData("gemini-3.8-flash-lite-tts", false)]
    [InlineData("gemini-3.8-flash-lite-tts", true)]
    public async Task GenerateAudioAsync_StructuredModelsKeepTurnsAndVoicesInOneRequest(string model, bool adminTest)
    {
        var handler = new PreviewAudioHandler();
        var service = CreateService(handler, new("text", model, null, "unused-fallback"));
        // Consecutive turns and Speaker2 first ensure mapping uses the dialogue's
        // speaker IDs, not line position or alternating voices.
        DialogueLine[] lines = [new("Speaker2", "你好。"), new("Speaker2", "想喝什麼？"), new("Speaker1", "茶，謝謝。")];
        var dialogue = new GeneratedDialogue("Chat", "Kore", "Puck", lines, []);
        var session = adminTest ? new GeminiTestSession("text", model, ["test-key"]) : null;

        await service.GenerateAudioAsync(dialogue, "zh-TW", testSession: session);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests));
        var content = Assert.Single(document.RootElement.GetProperty("contents").EnumerateArray());
        var parts = content.GetProperty("parts");
        Assert.Equal(lines.Length, parts.GetArrayLength());
        var voices = document.RootElement.GetProperty("generationConfig").GetProperty("speechConfig")
            .GetProperty("multiSpeakerVoiceConfig").GetProperty("speakerVoiceConfigs")
            .EnumerateArray().ToDictionary(v => v.GetProperty("speaker").GetString()!,
                v => v.GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
        for (var i = 0; i < lines.Length; i++)
        {
            Assert.Equal(lines[i].Text, parts[i].GetProperty("text").GetString());
            var metadata = parts[i].GetProperty("speech_metadata");
            Assert.Equal(lines[i].Speaker, metadata.GetProperty("speaker").GetString());
            Assert.Contains("Taiwan", metadata.GetProperty("style").GetString());
            Assert.Equal(lines[i].Speaker == "Speaker1" ? "Kore" : "Puck", voices[lines[i].Speaker]);
        }
        if (session is not null)
            Assert.Contains("speech_metadata", session.ToJson());
    }

    [Theory]
    [InlineData("gemini-3.8-flash-tts", "gemini-3.1-flash-tts-preview")]
    [InlineData("gemini-3.1-flash-tts-preview", "gemini-3.8-flash-lite-tts")]
    public async Task GenerateAudioAsync_FallbackUsesItsOwnRequestFormat(string primary, string fallback)
    {
        var handler = new PreviewAudioHandler(HttpStatusCode.ServiceUnavailable);
        var service = CreateService(handler, new("text", primary, null, fallback));
        var dialogue = new GeneratedDialogue("Chat", "Kore", "Puck",
            [new("Speaker1", "Hello."), new("Speaker2", "Hi.")], []);

        await service.GenerateAudioAsync(dialogue, "en-US");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(primary.Contains("3.8"), handler.Requests[0].Contains("speech_metadata"));
        Assert.Equal(fallback.Contains("3.8"), handler.Requests[1].Contains("speech_metadata"));
    }

    [Theory]
    [InlineData("gemini-3.8-flash-tts")]
    [InlineData("gemini-3.8-flash-lite-tts")]
    public async Task AudioTest_StructuredModelErrorPreservesStatusWithoutRetryOrFallback(string model)
    {
        var handler = new PreviewAudioHandler(HttpStatusCode.BadRequest);
        var service = CreateService(handler, new("text", "default-tts", null, "fallback-tts"),
            ["first-key", "second-key"]);
        var session = new GeminiTestSession("text", model, ["first-key", "second-key"]);
        var dialogue = new GeneratedDialogue("Chat", "Kore", "Puck",
            [new("Speaker1", "Hello."), new("Speaker2", "Hi.")], []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAudioAsync(dialogue, "en-US", testSession: session));

        Assert.Single(handler.Requests);
        using var details = JsonDocument.Parse(session.ErrorJson(error, "tts"));
        Assert.Equal(400, details.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.Contains("no retry or fallback", error.Message);
        Assert.Contains(model, session.ToJson());
    }

    [Theory]
    [InlineData("gemini-3.8-flash-tts")]
    [InlineData("gemini-3.8-flash-lite-tts")]
    public async Task GenerateAudioAsync_UsesRegionalVoiceIdsAndRecordsTheirAccents(string model)
    {
        var handler = new PreviewAudioHandler(regional: true);
        var service = CreateService(handler, new("text", model, null, null));
        var session = new GeminiTestSession("text", model, ["test-key"]);
        var dialogue = new GeneratedDialogue("Chat", "Kore", "Puck",
            [new("Speaker1", "Hello."), new("Speaker2", "Hi.")], []);
        await service.GenerateAudioAsync(dialogue, "en-AU", testSession: session);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests));
        var voices = body.RootElement.GetProperty("generationConfig").GetProperty("speechConfig")
            .GetProperty("multiSpeakerVoiceConfig").GetProperty("speakerVoiceConfigs");
        Assert.Equal("au-female", voices[0].GetProperty("voiceConfig").GetProperty("voice").GetString());
        Assert.Equal("au-male", voices[1].GetProperty("voiceConfig").GetProperty("voice").GetString());
        Assert.DoesNotContain("prebuiltVoiceConfig", handler.Requests[0]);
        var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
        for (var i = 0; i < dialogue.Lines.Length; i++)
        {
            // Matching a native voice must not remove the accent instructions.
            Assert.Equal(dialogue.Lines[i].Text, parts[i].GetProperty("text").GetString());
            var style = parts[i].GetProperty("speech_metadata").GetProperty("style").GetString();
            Assert.Contains("English (Australia)", style);
            Assert.Contains("strong, clearly recognisable native regional accent", style);
        }
        Assert.Contains("speechStyle", session.ToJson());
        Assert.Contains("Australian", session.ToJson());
        Assert.Contains("voice_selection", session.ToJson());
    }

    [Fact]
    public async Task GenerateDialogueAsync_UsesFallbackTextModelAfterPrimaryFails()
    {
        var handler = new FallbackHandler(audio: false);
        var service = CreateService(handler, new AiModelSelection("primary-text", "primary-tts", "backup-text", null));

        var dialogue = await service.GenerateDialogueAsync("English", "en-US", "ordering coffee", 60);

        Assert.NotEmpty(dialogue.Lines);
        Assert.Equal(["primary-text", "backup-text"], handler.Models);
    }

    [Fact]
    public async Task DisconnectedChatGptUsesGeminiTextFallback()
    {
        var handler = new FallbackHandler(audio: false);
        var service = CreateService(handler, new("chatgpt/account-model", "tts", "backup-text", null));
        var dialogue = await service.GenerateDialogueAsync("English", "en-US", "ordering coffee", 60);
        Assert.NotEmpty(dialogue.Lines);
        Assert.Equal(["backup-text"], handler.Models);
    }

    [Fact]
    public async Task GenerateAudioAsync_UsesFallbackTtsModelAfterPrimaryFails()
    {
        var handler = new FallbackHandler(audio: true);
        var service = CreateService(handler, new AiModelSelection("primary-text", "primary-tts", null, "backup-tts"));
        var dialogue = new GeneratedDialogue("Coffee", "Kore", "Puck", [new DialogueLine("Speaker1", "Hello")], []);

        var audio = await service.GenerateAudioAsync(dialogue, "en-US");

        Assert.Equal("audio/wav", audio.ContentType);
        Assert.Equal(["primary-tts", "backup-tts"], handler.Models);
    }

    [Fact]
    public async Task GenerateDialogueAsync_ModelOverloadSkipsRemainingPrimaryKeys()
    {
        var handler = new FallbackHandler(audio: false);
        var service = CreateService(handler, new("primary-text", "tts", "backup-text", null),
            ["first-key", "second-key", "third-key"]);

        await service.GenerateDialogueAsync("English", "en-GB", "a delayed bus", 60);

        Assert.Equal(["primary-text", "backup-text"], handler.Models);
    }

    [Fact]
    public async Task GenerateAudioAsync_ModelOverloadSkipsRemainingPrimaryKeys()
    {
        var handler = new FallbackHandler(audio: true);
        var service = CreateService(handler, new("text", "primary-tts", null, "backup-tts"),
            ["first-key", "second-key", "third-key"]);
        var dialogue = new GeneratedDialogue("Chat", "Kore", "Puck", [new("Speaker1", "Hello")], []);

        await service.GenerateAudioAsync(dialogue, "en-GB");

        Assert.Equal(["primary-tts", "backup-tts"], handler.Models);
    }

    [Fact]
    public async Task GenerateDialogueAsync_OverloadWithoutFallbackDoesNotRotateOrDisableKeys()
    {
        var handler = new FallbackHandler(audio: false);
        var service = CreateService(handler, new("primary-text", "tts", null, null),
            ["first-key", "second-key", "third-key"]);

        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.GenerateDialogueAsync("English", "en-GB", "a delayed bus", 60));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, Assert.IsType<HttpRequestException>(error.InnerException).StatusCode);
        }
        Assert.Equal(["primary-text", "primary-text"], handler.Models);
    }

    [Theory]
    [InlineData("restricted_topic", "This topic cannot be used for generation.")]
    [InlineData("no_suitable_news", "No suitable recent news was found.")]
    [InlineData("same_gender_required", "This scenario requires two speakers of the same gender.")]
    [InlineData("unexpected_provider_detail", "This scenario could not be generated.")]
    [InlineData(null, "This scenario could not be generated.")]
    public async Task GenerateDialogueAsync_PreservesRejectionDiagnosticsWithoutExposingThem(
        string? reason, string expectedMessage)
    {
        const string explanation = "Provider-only diagnostic detail";
        var response = JsonSerializer.Serialize(new { rejected = true, reason, explanation });
        var handler = new CapturingHandler(responseDialogue: response);
        var service = CreateService(handler, new("primary-text", "tts", "backup-text", null));

        var error = await Assert.ThrowsAsync<ContentRejectedException>(() =>
            service.GenerateDialogueAsync("English", "en-GB", "a rejected scenario", 60));

        Assert.Equal(3, handler.RequestCount);
        Assert.StartsWith(expectedMessage, error.Message);
        Assert.DoesNotContain(explanation, error.Message);
        Assert.DoesNotContain("unexpected_provider_detail", error.Message);
        Assert.Equal(reason is "restricted_topic" or "no_suitable_news" or "same_gender_required" ? reason : "unspecified", error.Reason);
        Assert.Equal(explanation, error.Explanation);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GenerateDialogueAsync_NonQuotaFailureSkipsRemainingKeys(HttpStatusCode status)
    {
        var handler = new FallbackHandler(audio: false, primaryStatus: status);
        var service = CreateService(handler, new("primary-text", "tts", "backup-text", null),
            ["first-key", "second-key", "third-key"]);

        await service.GenerateDialogueAsync("English", "en-GB", "a delayed bus", 60);

        Assert.Equal(["primary-text", "backup-text"], handler.Models);
    }

    [Fact]
    public async Task GenerateDialogueAsync_QuotaTriesAllEligibleKeysBeforeFallback()
    {
        var handler = new FallbackHandler(audio: false, primaryStatus: HttpStatusCode.TooManyRequests);
        var service = CreateService(handler, new("primary-text", "tts", "backup-text", null),
            ["first-key", "second-key", "third-key"]);

        await service.GenerateDialogueAsync("English", "en-GB", "a delayed bus", 60);

        Assert.Equal(["primary-text", "primary-text", "primary-text", "backup-text"], handler.Models);
    }

    [Fact]
    public async Task GenerateDialogueAsync_OverloadedFallbackAlsoStopsKeyRotation()
    {
        var handler = new FallbackHandler(audio: false);
        var service = CreateService(handler, new("primary-text", "tts", "primary-backup", null),
            ["first-key", "second-key", "third-key"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateDialogueAsync("English", "en-GB", "a delayed bus", 60));

        Assert.Equal(["primary-text", "primary-backup"], handler.Models);
    }

    [Fact]
    public async Task GenerateDialogueAsync_RetriesTopicRejectionWithoutChangingModel()
    {
        var handler = new FallbackHandler(audio: false, rejectPrimary: true);
        var service = CreateService(handler, new AiModelSelection("primary-text", "primary-tts", "backup-text", null));

        await Assert.ThrowsAsync<ContentRejectedException>(() =>
            service.GenerateDialogueAsync("English", "en-US", "a rejected topic", 60));

        Assert.Equal(["primary-text", "primary-text", "primary-text"], handler.Models);
    }

    [Fact]
    public async Task GenerateDialogueAsync_RecoversFromOneTopicRejection()
    {
        var handler = new RejectionOnceHandler();
        var service = CreateService(handler);

        var dialogue = await service.GenerateDialogueAsync("English", "en-US", "ordering coffee", 60);

        Assert.NotEmpty(dialogue.Lines);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("Reconsider whether a neutral everyday interpretation", handler.LastPrompt);
        Assert.Contains("Do not invent political connections", handler.LastPrompt);
        Assert.DoesNotContain("any honest interpretation", handler.LastPrompt);
    }

    [Fact]
    public async Task GenerateDialogueAsync_SkipsQuotaLimitedKeyOnNextRequest()
    {
        var handler = new QuotaOnceHandler();
        var service = CreateService(handler, apiKeys: ["first-project-key", "second-project-key"]);

        await service.GenerateDialogueAsync("English", "en-US", "ordering coffee", 60);
        await service.GenerateDialogueAsync("English", "en-US", "ordering coffee", 60);

        Assert.Equal(3, handler.Keys.Count);
        Assert.NotEqual(handler.Keys[0], handler.Keys[1]);
        Assert.Equal(handler.Keys[1], handler.Keys[2]);
    }

    [Fact]
    public void UpdateAiSettingsRequest_DistinguishesOldClientsFromClearingFallbacks()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var oldClient = JsonSerializer.Deserialize<UpdateAiSettingsRequest>(
            "{\"textModel\":null,\"audioModel\":null}", jsonOptions)!;
        var clearFallbacks = JsonSerializer.Deserialize<UpdateAiSettingsRequest>(
            "{\"textModel\":null,\"audioModel\":null,\"fallbackTextModel\":null,\"fallbackAudioModel\":null}", jsonOptions)!;

        Assert.Equal(JsonValueKind.Undefined, oldClient.FallbackTextModel.ValueKind);
        Assert.Equal(JsonValueKind.Undefined, oldClient.FallbackAudioModel.ValueKind);
        Assert.Equal(JsonValueKind.Null, clearFallbacks.FallbackTextModel.ValueKind);
        Assert.Equal(JsonValueKind.Null, clearFallbacks.FallbackAudioModel.ValueKind);
    }

    [Fact]
    public async Task AudioTest_FailureUsesOnlySelectedModelAndOneKey()
    {
        var handler = new FallbackHandler(audio: true);
        var service = CreateService(handler, new("primary-text", "default-audio", "fallback-text", "fallback-audio"),
            ["first-key", "second-key"]);
        var session = new GeminiTestSession("test-text", "primary-selected-tts", ["first-key", "second-key"]);
        var dialogue = new GeneratedDialogue("Test", "Kore", "Puck",
            [new("Speaker1", "Hello."), new("Speaker2", "Hi.")], []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAudioAsync(dialogue, "en-US", testSession: session));

        Assert.Equal(["primary-selected-tts"], handler.Models);
        Assert.Contains("no retry or fallback", error.Message);
        using var diagnostics = JsonDocument.Parse(session.ToJson());
        var call = Assert.Single(diagnostics.RootElement.EnumerateArray());
        Assert.Equal(503, call.GetProperty("httpStatus").GetInt32());
        Assert.Contains("Provider failure", call.GetProperty("responseBody").GetString());
    }

    [Fact]
    public async Task AudioTest_RejectionIsRecordedWithoutRetryingContent()
    {
        var handler = new RejectionOnceHandler();
        var service = CreateService(handler);
        var session = new GeminiTestSession("test-text", "test-tts", ["test-key"]);
        await Assert.ThrowsAsync<ContentRejectedException>(() => service.GenerateDialogueAsync(
            "English", "en-US", "", 60, testSession: session));
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("rejected", session.ToJson());
    }

    [Fact]
    public async Task AudioTest_GenderValidationDoesNotRetry()
    {
        var handler = new CapturingHandler([("female", "female"), ("female", "male")]);
        var service = CreateService(handler, apiKeys: ["first-key", "second-key"]);
        var session = new GeminiTestSession("test-text", "test-tts", ["first-key", "second-key"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateDialogueAsync(
            "English", "en-US", "", 240, testSession: session));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task AudioTest_QuotaDoesNotTryAnotherKey()
    {
        var handler = new QuotaOnceHandler();
        var service = CreateService(handler, apiKeys: ["first-key", "second-key"]);
        var session = new GeminiTestSession("test-text", "test-tts", ["first-key", "second-key"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateDialogueAsync(
            "English", "en-US", "", 120, testSession: session));
        Assert.Single(handler.Keys);
        Assert.Contains("429", session.ToJson());
    }

    [Theory]
    [InlineData(288)]
    [InlineData(1000)]
    public async Task Duration_FirstValidDraftProceedsToAudioWithoutLengthCorrections(int initialWords)
    {
        var handler = new DurationHandler([initialWords, 700]);
        var service = CreateService(handler);
        var dialogue = await service.GenerateDialogueAsync("English", "en-NZ", "coffee", 240);
        var audio = await service.GenerateAudioAsync(dialogue, "en-NZ");

        Assert.Single(handler.TextRequests);
        Assert.Equal(initialWords, dialogue.DurationPlan!.Count(dialogue.Lines));
        Assert.False(dialogue.DurationPlan.IsAcceptable(initialWords));
        Assert.Equal(1, handler.AudioRequests);
        Assert.Equal(200, audio.DurationMs);
    }

    [Fact]
    public async Task Duration_EmptyDialogueStillFailsBeforeSpeech()
    {
        var handler = new DurationHandler([0]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(handler).GenerateDialogueAsync("English", "en-NZ", "coffee", 240));
        Assert.Contains("no spoken text", error.Message);
        Assert.Single(handler.TextRequests);
        Assert.Equal(0, handler.AudioRequests);
    }

    [Fact]
    public async Task Duration_ShortNewsDraftProceedsWithoutAnotherSearchOrRevision()
    {
        var handler = new DurationHandler([288, 700]);
        var dialogue = await CreateService(handler, apiKeys: ["AIzaSy-test-search-key"]).GenerateDialogueAsync("English", "en-NZ", "", 240, "latest_news");
        Assert.Contains("google_search", Assert.Single(handler.TextRequests));
        Assert.Equal(288, dialogue.DurationPlan!.Count(dialogue.Lines));
    }

    [Theory]
    [InlineData("yue-CN")]
    [InlineData("zh-HK")]
    [InlineData("zh-CN")]
    public async Task Duration_ShortBeginnerChineseNewsKeepsTheFirstDraft(string locale)
    {
        // 54 five-letter tokens count as 270 characters, below the 303-character minimum.
        var handler = new DurationHandler([54]);
        var service = CreateService(handler, apiKeys: ["AIzaSy-test-search-key"]);
        var dialogue = await service.GenerateDialogueAsync("Chinese", locale, "", 120, "latest_news", level: "beginner");
        await service.GenerateAudioAsync(dialogue, locale);
        Assert.Single(handler.TextRequests);
        Assert.Equal(270, dialogue.DurationPlan!.Count(dialogue.Lines));
        Assert.Equal(303, dialogue.DurationPlan.MinimumUnits);
        Assert.Equal("beginner", dialogue.Level);
        Assert.Equal(1, handler.AudioRequests);
    }

    [Fact]
    public async Task Duration_AdminTestKeepsOneAttemptAndExposesShortDraft()
    {
        var handler = new DurationHandler([288]);
        var session = new GeminiTestSession("test-text", "test-tts", ["test-key"]);
        var dialogue = await CreateService(handler).GenerateDialogueAsync("English", "en-NZ", "coffee", 240,
            testSession: session);
        Assert.Single(handler.TextRequests);
        Assert.Equal(288, dialogue.DurationPlan!.Count(dialogue.Lines));
        Assert.DoesNotContain("DurationPlan", JsonSerializer.Serialize(dialogue));
    }

    [Fact]
    public async Task Duration_AcceptedDraftDoesNotNeedTextCorrection()
    {
        var handler = new DurationHandler([700]);
        await CreateService(handler).GenerateDialogueAsync("English", "en-NZ", "coffee", 240);
        Assert.Single(handler.TextRequests);
    }

    [Theory]
    [InlineData(30, 200)]
    [InlineData(500, 150000)]
    public async Task HistoricalTargetNeverRetriesShortOrLongScriptOrAudio(int words, int audioDurationMs)
    {
        var handler = new DurationHandler([words], audioDurationMs);
        var service = CreateService(handler, durationPlanner: new SampleDurationPlanner());
        var dialogue = await service.GenerateDialogueAsync("English", "en-NZ", "coffee", 60);
        var audio = await service.GenerateAudioAsync(dialogue, "en-NZ");
        var request = Assert.Single(handler.TextRequests);
        Assert.Contains("Script length target: 160 words", request);
        Assert.Equal(3, dialogue.DurationPlan!.HistorySampleCount);
        Assert.Equal(words, dialogue.DurationPlan.Count(dialogue.Lines));
        Assert.Equal(1, handler.AudioRequests);
        Assert.Equal(audioDurationMs, audio.DurationMs);
    }

    private sealed class SampleDurationPlanner : IDialogueDurationPlanner
    {
        public Task<DialogueDurationPlan> CreateAsync(string languageCode, int seconds, string level,
            string audioModel, CancellationToken ct) => Task.FromResult(
                HistoricalDialogueDurationPlanner.SelectPlan(languageCode, seconds, level, audioModel,
                    Enumerable.Repeat(new DialogueRateSample(languageCode, level,
                        string.Join(" ", Enumerable.Repeat("hello", 160)), 60000, audioModel), 3)));
    }

    private static string DialogueJson(int words, string first = "female", string second = "male") =>
        JsonSerializer.Serialize(new
        {
            title = "Coffee", speaker1_gender = first, speaker2_gender = second,
            lines = Enumerable.Range(0, words).Chunk(10).Select((chunk, i) => new
            {
                speaker = i % 2 == 0 ? "Speaker1" : "Speaker2",
                text = string.Join(" ", chunk.Select(_ => "hello")),
                shortCues = new[] { string.Join(" ", chunk.Select(_ => "hello")) },
            }).ToArray(),
        });

    private sealed class DurationHandler(int[] wordCounts, int audioDurationMs = 200) : HttpMessageHandler
    {
        public List<string> TextRequests { get; } = [];
        public List<string> Urls { get; } = [];
        public int AudioRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            object part;
            if (json.Contains("responseModalities"))
            {
                AudioRequests++;
                part = new { inlineData = new { data = Convert.ToBase64String(new byte[audioDurationMs * 48]), mimeType = "audio/L16;codec=pcm;rate=24000" } };
            }
            else
            {
                var count = wordCounts[Math.Min(TextRequests.Count, wordCounts.Length - 1)];
                TextRequests.Add(json);
                Urls.Add(request.RequestUri!.ToString());
                part = new { text = DialogueJson(count) };
            }
            var body = JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { part } } } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static GeminiService CreateService(
        HttpMessageHandler handler, AiModelSelection? selection = null, string[]? apiKeys = null,
        IDialogueDurationPlanner? durationPlanner = null)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://gemini.test"),
        };

        var settings = Options.Create(new GeminiSettings
        {
            ApiKeys = apiKeys ?? ["test-key"],
            TextModel = "test-text-model",
            LatestNewsTextModel = "test-news-model",
        });

        var services = new ServiceCollection().BuildServiceProvider();
        var cache = new MemoryCache(new MemoryCacheOptions());
        if (selection is not null) cache.Set("ai-model-settings", selection);
        var modelSettings = new AiModelSettingsService(
            services.GetRequiredService<IServiceScopeFactory>(),
            cache,
            settings,
            NullLogger<AiModelSettingsService>.Instance);
        var keyHealth = new GeminiKeyHealthService(
            services.GetRequiredService<IServiceScopeFactory>(),
            cache,
            settings,
            NullLogger<GeminiKeyHealthService>.Instance);

        return new GeminiService(
            new StaticHttpClientFactory(client),
            settings,
            modelSettings,
            keyHealth,
            new GeminiVoiceCatalog(new StaticHttpClientFactory(client), cache),
            new BanteraApi.Audio.Mp3Encoder(NullLogger<BanteraApi.Audio.Mp3Encoder>.Instance),
            new BanteraApi.Diagnostics.AiPipelineEventRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BanteraApi.Diagnostics.AiPipelineEventRecorder>.Instance),
            durationPlanner ?? new HistoricalDialogueDurationPlanner(
                services.GetRequiredService<IServiceScopeFactory>(), cache,
                NullLogger<HistoricalDialogueDurationPlanner>.Instance),
            NullLogger<GeminiService>.Instance);
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class PreviewAudioHandler(HttpStatusCode? firstFailure = null, bool regional = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(regional
                    ? """{"voices":[{"id":"au-female","language_code":"en-AU","gender":"female","accent":"Australian"},{"id":"au-male","language_code":"en-AU","gender":"male","accent":"Australian"}]}"""
                    : "{\"voices\":[]}") };
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Requests.Count == 1 && firstFailure is { } status)
                return new HttpResponseMessage(status) { Content = new StringContent("Provider error") };
            var body = JsonSerializer.Serialize(new
            {
                candidates = new[] { new { content = new { parts = new[]
                {
                    new { inlineData = new
                    {
                        data = Convert.ToBase64String(new byte[9600]),
                        mimeType = "audio/L16;codec=pcm;rate=24000",
                    } },
                } } } },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FallbackHandler(bool audio, bool rejectPrimary = false, HttpStatusCode primaryStatus = HttpStatusCode.ServiceUnavailable) : HttpMessageHandler
    {
        public List<string> Models { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var model = request.RequestUri!.AbsolutePath.Split("/models/")[1].Split(':')[0];
            Models.Add(model);
            if (model.StartsWith("primary", StringComparison.Ordinal))
            {
                if (!rejectPrimary)
                    return Task.FromResult(new HttpResponseMessage(primaryStatus)
                    {
                        Content = new StringContent("Provider failure"),
                    });
                return Task.FromResult(JsonResponse("{\"rejected\":true}"));
            }

            if (audio)
            {
                var body = JsonSerializer.Serialize(new
                {
                    candidates = new[] { new { content = new { parts = new[]
                    {
                        new { inlineData = new { data = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }), mimeType = "audio/wav" } },
                    } } } },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(JsonResponse(DialogueJson(175)));
        }

        private static HttpResponseMessage JsonResponse(string text)
        {
            var body = JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } } } } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class QuotaOnceHandler : HttpMessageHandler
    {
        public List<string> Keys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.Query.Split("key=")[1].Split('&')[0];
            Keys.Add(key);
            if (Keys.Count == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("RESOURCE_EXHAUSTED"),
                });

            var dialogue = DialogueJson(175);
            var body = JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = dialogue } } } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RejectionOnceHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string LastPrompt { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            LastPrompt = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            var text = RequestCount == 1
                ? "{\"rejected\":true,\"reason\":\"other\"}"
                : DialogueJson(175);
            var response = JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CapturingHandler((string First, string Second)[]? genders = null, string? responseDialogue = null) : HttpMessageHandler
    {
        private string? requestJson;
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            var pair = genders is { Length: > 0 }
                ? genders[Math.Min(RequestCount, genders.Length - 1)]
                : ("female", "male");
            RequestCount++;

            var target = int.Parse(System.Text.RegularExpressions.Regex.Match(GetPrompt(),
                @"Script length target: (\d+)").Groups[1].Value);
            var dialogueJson = responseDialogue ?? DialogueJson(target, pair.Item1, pair.Item2);

            var responseJson = JsonSerializer.Serialize(new
            {
                candidates = new[]
                {
                    new
                    {
                        content = new
                        {
                            parts = new[]
                            {
                                new { text = dialogueJson },
                            },
                        },
                    },
                },
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }

        public string GetPrompt()
        {
            Assert.False(string.IsNullOrWhiteSpace(requestJson));

            using var doc = JsonDocument.Parse(requestJson);
            return doc.RootElement
                .GetProperty("contents")[0]
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString()!;
        }
    }
}

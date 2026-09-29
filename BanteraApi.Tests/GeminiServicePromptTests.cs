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
    [Theory]
    [InlineData("beginner", "Difficulty: Beginner", "simple grammar")]
    [InlineData("intermediate", "Difficulty: Intermediate", "accessible sentences")]
    [InlineData("advanced", "Difficulty: Advanced", "nuanced opinions")]
    public async Task DialogueLevelChangesPromptAndSurvivesIntoSpeech(string level, string heading, string instruction)
    {
        var handler = new CapturingHandler();
        var dialogue = await CreateService(handler).GenerateDialogueAsync("English", "en-NZ", "coffee", 60, level: level);
        Assert.Equal(level, dialogue.Level);
        Assert.Contains(heading, handler.GetPrompt());
        Assert.Contains(instruction, handler.GetPrompt());
        if (level == "advanced")
        {
            Assert.DoesNotContain("Keep sentences short", handler.GetPrompt());
            Assert.DoesNotContain("Keep the language plain", handler.GetPrompt());
        }
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
    public async Task GenerateDialogueAsync_LatestNewsPrompt_AllowsFewerStoriesThanRequested()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);

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
        Assert.Contains("acceptable range: 315-385", prompt);
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
        Assert.Contains("unavailable", call.GetProperty("responseBody").GetString());
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
    [InlineData(288)] // Regression: the four-minute example produced only 1:37 of speech.
    [InlineData(1000)]
    public async Task Duration_RepairsTextBeforeGeneratingAudioOnce(int initialWords)
    {
        var handler = new DurationHandler([initialWords, 700]);
        var service = CreateService(handler);
        var dialogue = await service.GenerateDialogueAsync("English", "en-NZ", "coffee", 240);
        var audio = await service.GenerateAudioAsync(dialogue, "en-NZ");

        Assert.Equal(2, handler.TextRequests.Count);
        Assert.Contains("TEXT LENGTH CORRECTION", handler.TextRequests[1]);
        Assert.Contains(initialWords < 700 ? "Expand" : "Shorten", handler.TextRequests[1]);
        Assert.Equal(700, dialogue.DurationPlan!.Count(dialogue.Lines));
        Assert.Equal(1, handler.AudioRequests);
        // Even a very short successful TTS response never starts a duration regeneration loop.
        Assert.Equal(200, audio.DurationMs);
    }

    [Theory]
    [InlineData(288)]
    [InlineData(0)]
    public async Task Duration_StopsAfterTwoTextCorrectionsWithoutExtraProviderRetries(int words)
    {
        var handler = new DurationHandler([words]);
        var service = CreateService(handler, new("primary-text", "tts", "backup-text", null),
            ["first-key", "second-key"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateDialogueAsync("English", "en-NZ", "coffee", 240));
        Assert.Equal(3, handler.TextRequests.Count);
        Assert.All(handler.Urls, url =>
        {
            Assert.Contains("primary-text", url);
            Assert.True(url.Contains("first-key") || url.Contains("second-key"));
        });
        Assert.Equal(0, handler.AudioRequests);
    }

    [Fact]
    public async Task Duration_NewsRepairPreservesFactsWithoutAnotherSearch()
    {
        var handler = new DurationHandler([288, 700]);
        await CreateService(handler).GenerateDialogueAsync("English", "en-NZ", "", 240, "latest_news");
        Assert.Contains("google_search", handler.TextRequests[0]);
        Assert.DoesNotContain("google_search", handler.TextRequests[1]);
        Assert.Contains("do not search again or add new factual claims", handler.TextRequests[1]);
        Assert.Contains("Quoted previous draft", handler.TextRequests[1]);
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

    private sealed class DurationHandler(int[] wordCounts) : HttpMessageHandler
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
                part = new { inlineData = new { data = Convert.ToBase64String(new byte[9600]), mimeType = "audio/L16;codec=pcm;rate=24000" } };
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
        HttpMessageHandler handler, AiModelSelection? selection = null, string[]? apiKeys = null)
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

    private sealed class FallbackHandler(bool audio, bool rejectPrimary = false) : HttpMessageHandler
    {
        public List<string> Models { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var model = request.RequestUri!.AbsolutePath.Split("/models/")[1].Split(':')[0];
            Models.Add(model);
            if (model.StartsWith("primary", StringComparison.Ordinal))
            {
                if (!rejectPrimary)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("unavailable"),
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

    private sealed class CapturingHandler((string First, string Second)[]? genders = null) : HttpMessageHandler
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
            var dialogueJson = DialogueJson(target, pair.Item1, pair.Item2);

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

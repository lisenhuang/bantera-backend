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
        Assert.DoesNotContain("words total across all speakers", prompt);
    }

    [Fact]
    public async Task GenerateDialogueAsync_NonNewsPrompt_UsesDurationWithoutExplicitWordTarget()
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
        Assert.Contains("Use enough turns to fit the requested duration", prompt);
        Assert.DoesNotContain("Aim for approximately", prompt);
        Assert.DoesNotContain("words total across all speakers", prompt);
        Assert.Contains("One character must be male and the other female", prompt);
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
    public async Task GenerateDialogueAsync_DoesNotFallbackOnTopicRejection()
    {
        var handler = new FallbackHandler(audio: false, rejectPrimary: true);
        var service = CreateService(handler, new AiModelSelection("primary-text", "primary-tts", "backup-text", null));

        await Assert.ThrowsAsync<ContentRejectedException>(() =>
            service.GenerateDialogueAsync("English", "en-US", "a rejected topic", 60));

        Assert.Equal(["primary-text"], handler.Models);
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

    private static GeminiService CreateService(HttpMessageHandler handler, AiModelSelection? selection = null)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://gemini.test"),
        };

        var settings = Options.Create(new GeminiSettings
        {
            ApiKeys = ["test-key"],
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

        return new GeminiService(
            new StaticHttpClientFactory(client),
            settings,
            modelSettings,
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

            return Task.FromResult(JsonResponse("{\"title\":\"Coffee\",\"speaker1_gender\":\"female\",\"speaker2_gender\":\"male\",\"lines\":[{\"speaker\":\"Speaker1\",\"text\":\"Hello\",\"shortCues\":[\"Hello\"]}]}"));
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

            var dialogueJson = JsonSerializer.Serialize(new
            {
                title = "News Chat",
                speaker1_gender = pair.Item1,
                speaker1_styles = new[] { "friendly" },
                speaker2_gender = pair.Item2,
                speaker2_styles = new[] { "calm" },
                lines = new[]
                {
                    new
                    {
                        speaker = "Speaker1",
                        text = "Mia, did you see the latest science news?",
                        shortCues = new[] { "Mia, did you see the latest science news?" },
                    },
                    new
                    {
                        speaker = "Speaker2",
                        text = "Yes, Noah, it sounded useful.",
                        shortCues = new[] { "Yes, Noah, it sounded useful." },
                    },
                },
            });

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

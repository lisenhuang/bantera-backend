using System.Net;
using System.Text;
using System.Text.Json;
using BanteraApi.Gemini;
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
    }

    private static GeminiService CreateService(CapturingHandler handler)
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

        return new GeminiService(new StaticHttpClientFactory(client), settings);
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private string? requestJson;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);

            var dialogueJson = JsonSerializer.Serialize(new
            {
                title = "News Chat",
                speaker1_gender = "female",
                speaker1_styles = new[] { "friendly" },
                speaker2_gender = "male",
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

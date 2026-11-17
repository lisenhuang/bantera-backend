using System.Net;
using System.Text.Json;
using BanteraApi.OpenAi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace BanteraApi.Tests;

public sealed class OpenAiImageServiceTests
{
    [Fact]
    public async Task GeneratesFlareLowSquareJpeg_AndDecodesImage()
    {
        using var source = new Image<Rgba32>(16, 16);
        using var stream = new MemoryStream();
        await source.SaveAsJpegAsync(stream);
        var expected = stream.ToArray();
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.openai.com/v1/images/generations", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-secret", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("gpt-image-2.6-flare", root.GetProperty("model").GetString());
            Assert.Equal("low", root.GetProperty("quality").GetString());
            Assert.Equal("1024x1024", root.GetProperty("size").GetString());
            Assert.Equal("jpeg", root.GetProperty("output_format").GetString());
            Assert.Equal(1, root.GetProperty("n").GetInt32());
            Assert.Equal("A friendly avatar", root.GetProperty("prompt").GetString());
            return Json(JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(expected) } } }));
        });
        using var client = Client(handler);
        var actual = await Service(client).GenerateImageAsync("A friendly avatar");
        Assert.Equal(expected, actual);
        using var decoded = Image.Load(actual);
        Assert.Equal(16, decoded.Width);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task ProviderFailure_IsGeneric_AndNeverAutomaticallyRetried(int status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("private provider details test-secret") }));
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Service(client).GenerateImageAsync("cover"));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.DoesNotContain("private", error.ToString());
        Assert.DoesNotContain("test-secret", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("SET_VIA_ENV_OpenAiImages__ApiKey")]
    public async Task MissingConfiguration_DoesNotSendRequest(string key)
    {
        using var handler = new Handler((_, _) => throw new Exception("Must not send"));
        using var client = Client(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(client, key).GenerateImageAsync("cover"));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[{\"url\":\"https://example.com/image.jpg\"}]}")]
    [InlineData("{\"data\":[{\"b64_json\":\"\"}]}")]
    [InlineData("{\"data\":[{\"b64_json\":\"not base64!\"}]}")]
    [InlineData("{\"data\":[{},{}]}")]
    public async Task InvalidProviderResult_IsRejected(string body)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(body)));
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(client).GenerateImageAsync("cover"));
        Assert.Equal("Image generation returned an invalid result.", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Cancellation_IsPropagatedWithoutRetry()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new Handler(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        });
        using var client = Client(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(client).GenerateImageAsync("cover", cts.Token));
        Assert.Equal(1, handler.Calls);
    }

    [OpenAiImagesLiveFact]
    public async Task Live_ProfileAndCover_ReturnDecodableSquareImages()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri("https://api.openai.com/"),
            Timeout = TimeSpan.FromSeconds(120),
            MaxResponseContentBufferSize = 16 * 1024 * 1024
        };
        var service = Service(client, Environment.GetEnvironmentVariable("OpenAiImages__ApiKey")!);
        foreach (var prompt in new[]
        {
            "Friendly illustrated fictional adult profile avatar, centered face, lavender background, no text or logo.",
            "Colourful editorial illustration for a language lesson about ordering coffee, cafe counter and cup, no text or logo."
        })
        {
            var bytes = await service.GenerateImageAsync(prompt);
            using var image = Image.Load(bytes);
            Assert.Equal(1024, image.Width);
            Assert.Equal(1024, image.Height);
            Assert.Equal("JPEG", Image.DetectFormat(bytes).Name);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://api.openai.com/") };
    private static OpenAiImageService Service(HttpClient client, string key = "test-secret") =>
        new(new Factory(client), Options.Create(new OpenAiImageSettings { ApiKey = key }), NullLogger<OpenAiImageService>.Instance);

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(OpenAiImageService.ClientName, name);
            return client;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return send(request, ct);
        }
    }
}

public sealed class OpenAiImagesLiveFactAttribute : FactAttribute
{
    public OpenAiImagesLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BANTERA_TEST_OPENAI_IMAGES") != "true" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OpenAiImages__ApiKey")))
            Skip = "Opt-in paid OpenAI image smoke test; requires BANTERA_TEST_OPENAI_IMAGES and OpenAiImages__ApiKey.";
    }
}

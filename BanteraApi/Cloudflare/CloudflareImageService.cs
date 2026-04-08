using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace BanteraApi.Cloudflare;

public class CloudflareImageService(
    IHttpClientFactory httpClientFactory,
    IOptions<CloudflareSettings> options,
    ILogger<CloudflareImageService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const string Model = "@cf/black-forest-labs/flux-1-schnell";
    private const string HighlightStart = "\u001b[30;103m";
    private const string HighlightEnd = "\u001b[0m";

    private CloudflareSettings Settings => options.Value;

    public async Task<byte[]> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Settings.AccountId) || string.IsNullOrWhiteSpace(Settings.ApiToken))
        {
            WriteHighlightedTerminalMessage(
                "[AI Cover] Cloudflare image generation is misconfigured. Missing AccountId or ApiToken.");
            logger.LogError(
                "{HighlightStart}[AI Cover] Cloudflare image generation is misconfigured. Missing AccountId or ApiToken.{HighlightEnd}",
                HighlightStart,
                HighlightEnd);
            throw new InvalidOperationException("Cloudflare image generation is not configured.");
        }

        var client = httpClientFactory.CreateClient("cloudflare");
        var url = $"/client/v4/accounts/{Settings.AccountId}/ai/run/{Model}";

        var body = new { prompt, num_steps = 50, width = 512, height = 512 };
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Headers = { { "Authorization", $"Bearer {Settings.ApiToken}" } },
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            WriteHighlightedTerminalMessage(
                $"[AI Cover] Cloudflare image generation failed. Status={(int)response.StatusCode} Body={responseBody}");
            logger.LogError(
                "{HighlightStart}[AI Cover] Cloudflare image generation failed. Status={StatusCode} Body={ResponseBody}{HighlightEnd}",
                HighlightStart,
                (int)response.StatusCode,
                responseBody,
                HighlightEnd);
            response.EnsureSuccessStatusCode();
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
        if (contentType.Contains("application/json"))
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);
            var b64 = doc.RootElement
                .GetProperty("result")
                .GetProperty("image")
                .GetString()
                ?? throw new InvalidOperationException("Cloudflare returned JSON but no result.image field.");
            return Convert.FromBase64String(b64);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static void WriteHighlightedTerminalMessage(string message)
    {
        Console.Error.WriteLine($"{HighlightStart}{message}{HighlightEnd}");
    }
}

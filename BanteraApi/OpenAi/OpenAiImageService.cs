using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BanteraApi.OpenAi;

/// <summary>Shared image generation for profile avatars and audio lesson covers.</summary>
public sealed class OpenAiImageService(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAiImageSettings> options,
    ILogger<OpenAiImageService> logger)
{
    public const string ClientName = "openai-images";
    public const string Model = "gpt-image-2.6-flare";

    public async Task<byte[]> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var apiKey = options.Value.ApiKey.Trim();
        if (string.IsNullOrEmpty(apiKey) || apiKey.StartsWith("SET_VIA_ENV_", StringComparison.Ordinal))
            throw new InvalidOperationException("Image generation is not configured.");

        var client = httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = JsonContent.Create(new
            {
                model = Model,
                prompt,
                n = 1,
                size = "1024x1024",
                quality = "low",
                output_format = "jpeg"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var started = Stopwatch.GetTimestamp();
        // Do not retry paid generations automatically: a timeout can occur after billing.
        using var response = await client.SendAsync(request, cancellationToken);
        var requestId = response.Headers.TryGetValues("x-request-id", out var values)
            ? values.FirstOrDefault() : null;
        if (!response.IsSuccessStatusCode)
        {
            // Provider bodies can contain prompts or credentials; never expose them to clients/logs.
            logger.LogWarning("Image generation failed: Model={Model} Status={Status} RequestId={RequestId} ElapsedMs={ElapsedMs}",
                Model, (int)response.StatusCode, requestId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw new HttpRequestException("Image generation is temporarily unavailable.", null, response.StatusCode);
        }

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var data = document.RootElement.GetProperty("data");
            if (data.GetArrayLength() != 1)
                throw new InvalidOperationException("Image generation returned an invalid result.");
            var encoded = data[0].GetProperty("b64_json").GetString();
            if (string.IsNullOrWhiteSpace(encoded))
                throw new InvalidOperationException("Image generation returned an empty result.");
            var image = Convert.FromBase64String(encoded);
            if (image.Length == 0)
                throw new InvalidOperationException("Image generation returned an empty result.");
            logger.LogInformation("Image generation completed: Model={Model} Quality=low Size=1024x1024 Bytes={Bytes} RequestId={RequestId} ElapsedMs={ElapsedMs}",
                Model, image.Length, requestId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return image;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            logger.LogWarning("Image generation returned an invalid result: Model={Model} RequestId={RequestId}", Model, requestId);
            throw new InvalidOperationException("Image generation returned an invalid result.");
        }
    }
}

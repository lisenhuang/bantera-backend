using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BanteraApi.Cloudflare;

public class CloudflareImageService(IHttpClientFactory httpClientFactory, IOptions<CloudflareSettings> options)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const string Model = "@cf/black-forest-labs/flux-1-schnell";

    private CloudflareSettings Settings => options.Value;

    public async Task<byte[]> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("cloudflare");
        var url = $"/client/v4/accounts/{Settings.AccountId}/ai/run/{Model}";

        var body = new { prompt, num_steps = 50, width = 512, height = 512 };
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Headers = { { "Authorization", $"Bearer {Settings.ApiToken}" } },
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

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
}

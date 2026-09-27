using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace BanteraApi.Gemini;

/// <summary>Regional prebuilt voices are portable across projects; never cache credentials.</summary>
public sealed class GeminiVoiceCatalog(IHttpClientFactory clients, IMemoryCache cache)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public sealed record Voice(string Id, string LanguageCode, string Gender, string Accent, string Context);
    public sealed record Pair(Voice Speaker1, Voice Speaker2);

    public async Task<Pair?> ResolveAsync(string key, string languageCode, string gender1, string gender2,
        string seed1, string seed2, string model, GeminiTestSession? session, CancellationToken ct)
    {
        var locale = languageCode.ToLowerInvariant() switch
        {
            "zh-tw" => "cmn-Hant-TW",
            "zh-cn" => "cmn-Hans-CN",
            "zh-hk" => "yue-Hant-HK",
            _ => languageCode,
        };
        var cacheKey = $"gemini-voices:{locale.ToLowerInvariant()}";
        if (!cache.TryGetValue(cacheKey, out Voice[]? voices))
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!cache.TryGetValue(cacheKey, out voices))
                {
                    var found = new List<Voice>();
                    string? next = null;
                    var seenPages = new HashSet<string>();
                    do
                    {
                        var url = $"/v1beta/voices?type=prebuilt&language_code={Uri.EscapeDataString(locale)}&page_size=1000";
                        if (next is not null) url += $"&page_token={Uri.EscapeDataString(next)}";
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.Add("x-goog-api-key", key);
                        using var response = await clients.CreateClient("gemini").SendAsync(request, ct);
                        var json = await response.Content.ReadAsStringAsync(ct);
                        if (session is not null)
                            await session.CaptureAsync("voice_catalog", model, key, new { method = "GET", languageCode = locale, pageToken = next }, response, json);
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException($"Gemini voice catalogue failed with status {(int)response.StatusCode}. Body: {json[..Math.Min(json.Length, 1200)]}", null, response.StatusCode);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("voices", out var items))
                        foreach (var item in items.EnumerateArray())
                        {
                            string Read(string name) => item.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
                            var voice = new Voice(Read("id"), Read("language_code"), Read("gender").ToLowerInvariant(), Read("accent"), Read("context"));
                            // Check returned metadata as well as the query filter. Never substitute a different region.
                            if (voice.Id.Length > 0 && voice.LanguageCode.Equals(locale, StringComparison.OrdinalIgnoreCase)
                                && voice.Gender is "male" or "female") found.Add(voice);
                        }
                        next = doc.RootElement.TryGetProperty("next_page_token", out var token) ? token.GetString() : null;
                        if (string.IsNullOrEmpty(next)) break;
                        if (!seenPages.Add(next)) throw new InvalidOperationException("Gemini repeated a voice catalogue page token.");
                    } while (true);
                    voices = found.ToArray();
                    cache.Set(cacheKey, voices, voices.Length == 0 ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24));
                }
            }
            finally { gate.Release(); }
        }
        Voice? Pick(string gender, string seed)
        {
            var candidates = voices!.Where(v => v.Gender == gender).ToArray();
            var conversational = candidates.Where(v => v.Context.Contains("Conversational", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (conversational.Length > 0) candidates = conversational;
            candidates = candidates.OrderBy(v => v.Id, StringComparer.Ordinal).ToArray();
            if (candidates.Length == 0) return null;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return candidates[hash[0] % candidates.Length];
        }
        var first = Pick(gender1, seed1);
        var second = Pick(gender2, seed2);
        return first is null || second is null ? null : new Pair(first, second);
    }
}

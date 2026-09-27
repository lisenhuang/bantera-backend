using System.Net;
using BanteraApi.Gemini;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace BanteraApi.Tests;

public sealed class GeminiVoiceCatalogTests
{
    [Fact]
    public async Task MatchesRegionAndGenderAcrossPagesAndCachesWithoutProjectKeys()
    {
        var handler = new CatalogHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = new GeminiVoiceCatalog(new Factory(handler), cache);
        var first = await catalog.ResolveAsync("first-key", "en-AU", "female", "male", "Kore", "Puck", "gemini-3.8-flash-tts", null, default);
        Assert.NotNull(first);
        Assert.Equal("au-female", first.Speaker1.Id);
        Assert.Equal("au-male", first.Speaker2.Id);
        var second = await catalog.ResolveAsync("different-project-key", "en-AU", "female", "male", "Kore", "Puck", "gemini-3.8-flash-lite-tts", null, default);
        Assert.Equal(first, second);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, url => Assert.Contains("language_code=en-AU", url));
        Assert.DoesNotContain("first-key", string.Join(" ", handler.Requests));
    }

    [Fact]
    public async Task DoesNotPickAnotherRegionWhenRequestedPairIsMissing()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = new GeminiVoiceCatalog(new Factory(new CatalogHandler()), cache);
        Assert.Null(await catalog.ResolveAsync("key", "en-NZ", "male", "female", "Puck", "Kore", "gemini-3.8-flash-tts", null, default));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://gemini.test") };
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            var json = request.RequestUri.Query.Contains("page_token")
                ? """{"voices":[{"id":"au-male","language_code":"en-AU","gender":"male","accent":"Australian","context":"Conversational"}]}"""
                : """{"next_page_token":"page2","voices":[{"id":"us-female","language_code":"en-US","gender":"female"},{"id":"au-neutral","language_code":"en-AU","gender":"neutral"},{"id":"au-female","language_code":"en-AU","gender":"female","accent":"Australian","context":"Conversational"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}

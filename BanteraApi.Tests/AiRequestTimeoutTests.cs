using System.Text.Json;
using BanteraApi.Gemini;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
namespace BanteraApi.Tests;
public class AiRequestTimeoutTests
{
    [Theory]
    [InlineData("null", true, null)]
    [InlineData("30", true, 30)]
    [InlineData("180", true, 180)]
    [InlineData("300", true, 300)]
    [InlineData("29", false, null)]
    [InlineData("301", false, null)]
    [InlineData("180.5", false, null)]
    [InlineData("\"180\"", false, null)]
    [InlineData("{}", false, null)]
    public void AdminInputRequiresBoundedWholeSeconds(string json, bool valid, int? expected)
    {
        using var d = JsonDocument.Parse(json);
        Assert.Equal(valid, AiRequestTimeout.TryRead(d.RootElement, out var seconds));
        Assert.Equal(expected, seconds);
    }
    [Fact]
    public void OlderDashboardsCanOmitTheFieldAndInvalidStoredValuesUse180()
    {
        Assert.True(AiRequestTimeout.TryRead(default, out var seconds)); Assert.Null(seconds);
        foreach (var value in new string?[] { null, "", "garbage", "0", "301" }) Assert.Equal(180, AiRequestTimeout.FromStored(value));
        Assert.Equal(240, AiRequestTimeout.FromStored("240"));
        Assert.True(AiRequestTimeout.SearchTestBudgetSeconds(300) > 600);
    }
    [Fact]
    public async Task SettingsUseDefaultAndPreserveCachedOverrideAcrossSearchPolicy()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AiModelSettingsService(services.GetRequiredService<IServiceScopeFactory>(), cache, Options.Create(new GeminiSettings()), NullLogger<AiModelSettingsService>.Instance);
        Assert.Equal(180, (await service.GetAsync()).GptTimeoutSeconds);
        cache.Set("ai-model-settings", service.Defaults with { GptTimeoutSeconds = 240 });
        Assert.Equal(240, (await service.GetAsync()).GptTimeoutSeconds);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.UpdateAsync(null, null, Guid.NewGuid(), updateGptTimeout: true, gptTimeoutSeconds: 301));
    }
}

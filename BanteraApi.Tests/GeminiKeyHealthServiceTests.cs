using System.Net;
using BanteraApi.Gemini;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class GeminiKeyHealthServiceTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "RESOURCE_EXHAUSTED", GeminiKeyFailureKind.QuotaLimited)]
    [InlineData(HttpStatusCode.BadRequest, "API_KEY_INVALID", GeminiKeyFailureKind.InvalidKey)]
    [InlineData(HttpStatusCode.Forbidden, "API key was reported as leaked", GeminiKeyFailureKind.InvalidKey)]
    [InlineData(HttpStatusCode.Forbidden, "This API key is blocked", GeminiKeyFailureKind.InvalidKey)]
    [InlineData(HttpStatusCode.Unauthorized, "Authentication failed", GeminiKeyFailureKind.InvalidKey)]
    [InlineData(HttpStatusCode.BadRequest, "Invalid request body", GeminiKeyFailureKind.Other)]
    [InlineData(HttpStatusCode.Forbidden, "Model permission denied", GeminiKeyFailureKind.Other)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Overloaded", GeminiKeyFailureKind.Other)]
    public void Classify_OnlyDisablesConfirmedBadKeys(
        HttpStatusCode status, string message, GeminiKeyFailureKind expected)
    {
        Assert.Equal(expected, GeminiKeyHealthService.Classify(new HttpRequestException(message, null, status)));
    }

    [Fact]
    public async Task QuotaCooldown_SkipsOnlyAffectedKeyAndModel()
    {
        var health = CreateService();
        const string limitedKey = "test-key-one";
        const string availableKey = "test-key-two";

        var retryAt = health.CoolDown(limitedKey, "tts-model");

        Assert.True(retryAt > DateTimeOffset.UtcNow);
        Assert.Equal([availableKey], await health.EligibleKeysAsync([limitedKey, availableKey], "tts-model", default));
        Assert.Equal([limitedKey, availableKey], await health.EligibleKeysAsync([limitedKey, availableKey], "text-model", default));
        var snapshot = await health.GetSnapshotAsync(default);
        Assert.Equal(1, snapshot.Healthy);
        Assert.Equal("cooldown", Assert.Single(snapshot.Items).Status);
    }

    [Fact]
    public async Task InvalidKey_IsSkippedAcrossModels()
    {
        var health = CreateService();
        await health.MarkInvalidAsync("test-key-one", "Invalid, revoked, or blocked key", default);

        Assert.Equal(["test-key-two"], await health.EligibleKeysAsync(["test-key-one", "test-key-two"], "tts-model", default));
        Assert.Equal(["test-key-two"], await health.EligibleKeysAsync(["test-key-one", "test-key-two"], "text-model", default));
        Assert.Equal("invalid", Assert.Single((await health.GetSnapshotAsync(default)).Items).Status);
    }

    private static GeminiKeyHealthService CreateService()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new GeminiKeyHealthService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new GeminiSettings { ApiKeys = ["test-key-one", "test-key-two"] }),
            NullLogger<GeminiKeyHealthService>.Instance);
    }
}

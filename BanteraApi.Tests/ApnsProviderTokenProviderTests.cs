using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using BanteraApi.Chat;
using BanteraApi.Database.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public sealed class ApnsProviderTokenProviderTests
{
    [Fact]
    public async Task NotificationEnableAlertAndSubsequentCallsReuseTokenAcrossTransientClients()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = Options.Create(Settings(key));
        var clock = new TestClock();
        var handler = new Capture();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<ApnsSettings>>(options);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<ApnsProviderTokenProvider>();
        services.AddHttpClient<ChatPushNotificationService>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var container = services.BuildServiceProvider();
        var alerts = container.GetRequiredService<ChatPushNotificationService>();
        UserPushToken[] tokens = [new() { Token = "alert", Platform = "ios" },
            new() { Token = "call", Platform = "ios-voip", IsSandbox = true }];
        await alerts.SendAsync(tokens, "Notifications enabled", "Test", new Dictionary<string, string>());
        var first = Assert.Single(handler.Requests);
        clock.Now = clock.Now.AddSeconds(10);

        // Calls and ordinary notifications resolve different typed HTTP clients.
        // Signing a new JWT for each of these is what triggered Apple's 429 response.
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            var sender = container.GetRequiredService<ChatPushNotificationService>();
            Assert.NotSame(alerts, sender);
            await sender.SendAsync(tokens, "Caller", "Call", new Dictionary<string, string>(), voip: index % 2 == 0);
        }));
        Assert.Equal(13, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(first.Token, r.Token));
        Assert.Contains(handler.Requests, r => r.PushType == "voip");
        Assert.Contains(handler.Requests, r => r.PushType == "alert");
    }

    [Fact]
    public async Task ConcurrentRefreshRenewsOnceAtFiftyMinutesAndBeforeOneHourExpiry()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestClock();
        var provider = new ApnsProviderTokenProvider(Options.Create(Settings(key)), clock);
        var originalTime = clock.Now;
        var original = provider.GetToken();
        clock.Now = originalTime.AddMinutes(50).AddSeconds(-1);
        Assert.Equal(original, provider.GetToken());
        clock.Now = originalTime.AddMinutes(50);
        var renewed = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(provider.GetToken)));
        Assert.Single(renewed.Distinct());
        Assert.NotEqual(original, renewed[0]);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(renewed[0]);
        Assert.Equal(clock.Now.ToUnixTimeSeconds().ToString(), jwt.Claims.Single(c => c.Type == "iat").Value);
        Assert.Equal("team", jwt.Issuer);
        Assert.Equal("key", jwt.Header.Kid);
        Assert.Equal("ES256", jwt.Header.Alg);
        clock.Now = originalTime.AddHours(2);
        Assert.NotEqual(renewed[0], provider.GetToken());
    }

    private static ApnsSettings Settings(ECDsa key) => new()
    {
        KeyId = "key", TeamId = "team", BundleId = "test.bantera", PrivateKeyPem = key.ExportPkcs8PrivateKeyPem()
    };

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Capture : HttpMessageHandler
    {
        public ConcurrentBag<(string Token, string PushType)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Headers.Authorization!.Parameter!, request.Headers.GetValues("apns-push-type").Single()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

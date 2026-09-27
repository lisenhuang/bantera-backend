using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BanteraApi.Chat;
using BanteraApi.Database.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public sealed class ChatPushNotificationServiceTests
{
    [Fact]
    public async Task VoiceDm_RoutesDevelopmentAndProductionTokensIndependently()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var service = new ChatPushNotificationService(client, Options.Create(new ApnsSettings
        {
            KeyId = "test-key", TeamId = "test-team", BundleId = "test.bantera",
            PrivateKeyPem = key.ExportPkcs8PrivateKeyPem(),
            Environment = ApnsSettings.EnvironmentProduction,
        }), NullLogger<ChatPushNotificationService>.Instance);

        await service.SendAsync(
            [new() { Token = "development-token", IsSandbox = true },
             new() { Token = "production-token", IsSandbox = false }],
            "Sender", "sent an audio message",
            new Dictionary<string, string> { ["threadType"] = "dm", ["threadId"] = "thread", ["messageId"] = "message" });

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("api.sandbox.push.apple.com", handler.Requests[0].Host);
        Assert.Equal("api.push.apple.com", handler.Requests[1].Host);
        foreach (var request in handler.Requests)
        {
            Assert.Equal("alert", request.PushType);
            using var payload = JsonDocument.Parse(request.Body);
            Assert.Equal("default", payload.RootElement.GetProperty("aps").GetProperty("sound").GetString());
            Assert.Equal("sent an audio message", payload.RootElement.GetProperty("aps").GetProperty("alert").GetProperty("body").GetString());
            Assert.Equal("dm", payload.RootElement.GetProperty("threadType").GetString());
            Assert.Equal("message", payload.RootElement.GetProperty("messageId").GetString());
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(string Host, string PushType, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.Host, request.Headers.GetValues("apns-push-type").Single(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

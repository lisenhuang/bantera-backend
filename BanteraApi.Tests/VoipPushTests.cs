using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BanteraApi.Chat;
using BanteraApi.Database.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class VoipPushTests
{
    [Fact]
    public async Task VoipAndAlertsStayOnSeparateTransports()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new Capture();
        using var client = new HttpClient(handler);
        var service = new ChatPushNotificationService(client, Options.Create(new ApnsSettings {
            KeyId = "test", TeamId = "team", BundleId = "bantera.test", PrivateKeyPem = key.ExportPkcs8PrivateKeyPem()
        }), NullLogger<ChatPushNotificationService>.Instance);
        UserPushToken[] tokens = [new() {Token = "alert", Platform = "ios"},
            new() {Token = "voip-dev", Platform = "ios-voip", IsSandbox = true},
            new() {Token = "voip-prod", Platform = "ios-voip"}];
        var data = new Dictionary<string,string> { ["type"] = "incoming_call", ["callId"] = Guid.NewGuid().ToString() };
        await service.SendAsync(tokens, "Caller", "Incoming call", data, expiresAt: DateTimeOffset.UtcNow.AddSeconds(45), voip: true);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("api.sandbox.push.apple.com", handler.Requests[0].Host);
        Assert.Equal("api.push.apple.com", handler.Requests[1].Host);
        foreach (var request in handler.Requests) {
            Assert.Equal("voip", request.Type);
            Assert.Equal("bantera.test.voip", request.Topic);
            Assert.Equal("0", request.Expiry);
            using var json = JsonDocument.Parse(request.Body);
            Assert.Empty(json.RootElement.GetProperty("aps").EnumerateObject());
            Assert.Equal(data["callId"], json.RootElement.GetProperty("callId").GetString());
        }
        handler.Requests.Clear();
        await service.SendAsync(tokens, "Sender", "Voice message", data);
        var alert = Assert.Single(handler.Requests);
        Assert.Equal("alert", alert.Type);
        Assert.Equal("bantera.test", alert.Topic);
        using var alertJson = JsonDocument.Parse(alert.Body);
        Assert.Equal("Voice message", alertJson.RootElement.GetProperty("aps").GetProperty("alert").GetProperty("body").GetString());
    }

    [Fact]
    public async Task SuspendedCalleeCanReconnectAndAcceptButCallerDisconnectEndsCall()
    {
        var service = new ChatRealtimeService(NullLogger<ChatRealtimeService>.Instance);
        var caller = Guid.NewGuid(); var callee = Guid.NewGuid();
        Assert.True(service.TryCreateCall(caller, callee, "audio", out var call, false));
        await service.HandleUserDisconnectedAsync(callee);
        Assert.True(service.TryGetCall(call.CallId, callee, out _));
        Assert.False(service.TryGetCall(call.CallId, Guid.NewGuid(), out _));
        Assert.False(service.TryAcceptCall(call.CallId, caller, out _));
        Assert.True(service.TryAcceptCall(call.CallId, callee, out _));
        Assert.False(service.TryAcceptCall(call.CallId, callee, out _));
        await service.HandleUserDisconnectedAsync(caller);
        Assert.False(service.TryGetCall(call.CallId, callee, out _));
    }

    private sealed class Capture : HttpMessageHandler {
        public List<(string Host,string Type,string Topic,string? Expiry,string Body)> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add((request.RequestUri!.Host, request.Headers.GetValues("apns-push-type").Single(),
                request.Headers.GetValues("apns-topic").Single(),
                request.Headers.TryGetValues("apns-expiration", out var values) ? values.Single() : null,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

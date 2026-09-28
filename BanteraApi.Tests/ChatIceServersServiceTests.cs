using System.Net;
using System.Text;
using System.Text.Json;
using BanteraApi.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public sealed class ChatIceServersServiceTests
{
    private const string KeyId = "0123456789abcdef0123456789abcdef";
    private const string ValidResponse = """
        {"iceServers":[
          {"urls":["stun:stun.cloudflare.com:3478"]},
          {"urls":["turn:turn.cloudflare.com:3478?transport=udp",
                   "turn:turn.cloudflare.com:3478?transport=tcp",
                   "turns:turn.cloudflare.com:443?transport=tcp"],
           "username":"temporary-user","credential":"temporary-password"}
        ]}
        """;

    [Fact]
    public async Task RelayOnly_RemovesStunAndRequiresRelayPolicy()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(ValidResponse)));
        using var client = new HttpClient(handler);
        var response = await CreateService(client).GetAsync(relayOnly: true);
        Assert.Equal("relay", response.IceTransportPolicy);
        var relay = Assert.Single(response.IceServers);
        Assert.Equal("temporary-password", relay.Credential);
        Assert.All(relay.Urls, url => Assert.True(url.StartsWith("turn:") || url.StartsWith("turns:")));
    }

    [Theory]
    [InlineData(false, 201, ValidResponse)]
    [InlineData(true, 401, ValidResponse)]
    [InlineData(true, 500, ValidResponse)]
    [InlineData(true, 201, "invalid")]
    public async Task RelayOnly_Unavailable_DoesNotPermitDirectFallback(bool enabled, int status, string body)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        using var client = new HttpClient(handler);
        var settings = new CloudflareTurnSettings { Enabled = enabled, KeyId = KeyId, ApiToken = "secret" };
        var response = await CreateService(client, settings).GetAsync(relayOnly: true);
        Assert.Empty(response.IceServers);
        Assert.Equal("relay", response.IceTransportPolicy);
    }

    [Theory]
    [InlineData(null, "all", false)]
    [InlineData("", "all", false)]
    [InlineData("unknown", "all", false)]
    [InlineData("all", "all", true)]
    [InlineData("relay", "relay", true)]
    public void Policy_DefaultIsDirectWithFallback_AndUnknownUpdatesAreRejected(string? input, string resolved, bool valid)
    {
        Assert.Equal(resolved, ChatCallSettingsService.ResolvePolicy(input));
        Assert.Equal(valid, ChatCallSettingsService.IsValidPolicy(input));
    }

    [Fact]
    public async Task Configured_ReturnsTemporaryRelayCredentialsAndOriginalStun()
    {
        using var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"https://rtc.live.cloudflare.com/v1/turn/keys/{KeyId}/credentials/generate-ice-servers",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("server-only-secret", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(86400, body.RootElement.GetProperty("ttl").GetInt32());
            return JsonResponse(ValidResponse);
        });
        using var client = new HttpClient(handler);
        var result = await CreateService(client).GetAsync();

        Assert.Equal("stun:stun.l.google.com:19302", result.IceServers[0].Urls.Single());
        Assert.Equal(3, result.IceServers.Count);
        var relay = result.IceServers[2];
        Assert.Equal("temporary-user", relay.Username);
        Assert.Equal("temporary-password", relay.Credential);
        Assert.Contains("turns:turn.cloudflare.com:443?transport=tcp", relay.Urls);
        // Same JSON shape consumed by existing Flutter releases; permanent secret never leaves the server.
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"iceServers\"", json);
        Assert.Contains("\"credential\":\"temporary-password\"", json);
        Assert.DoesNotContain("server-only-secret", json);
        Assert.DoesNotContain(KeyId, json);
    }

    [Theory]
    [InlineData(false, KeyId, "secret", 86400)]
    [InlineData(true, "", "secret", 86400)]
    [InlineData(true, KeyId, "", 86400)]
    [InlineData(true, KeyId, "SET_VIA_ENV_CloudflareTurn__ApiToken", 86400)]
    [InlineData(true, "../invalid", "secret", 86400)]
    [InlineData(true, KeyId, "secret", 0)]
    [InlineData(true, KeyId, "secret", 86401)]
    public async Task DisabledOrInvalidConfig_PreservesStunWithoutProviderRequest(
        bool enabled, string keyId, string apiToken, int ttl)
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("Must not contact provider"));
        using var client = new HttpClient(handler);
        var settings = new CloudflareTurnSettings
        {
            Enabled = enabled, KeyId = keyId, ApiToken = apiToken, CredentialTtlSeconds = ttl,
        };
        AssertStunOnly(await CreateService(client, settings).GetAsync());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task ProviderFailure_PreservesStun(int status)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var client = new HttpClient(handler);
        AssertStunOnly(await CreateService(client).GetAsync());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"iceServers\":[]}")]
    [InlineData("{\"iceServers\":[null]}")]
    [InlineData("{\"iceServers\":[{\"urls\":null}]}")]
    [InlineData("{\"iceServers\":[{\"urls\":[null]}]}")]
    [InlineData("{\"iceServers\":[{\"urls\":[\"stun:stun.cloudflare.com:3478\"]}]}")]
    [InlineData("{\"iceServers\":[{\"urls\":[\"turn:turn.cloudflare.com:3478\"],\"username\":\"u\"}]}")]
    [InlineData("{\"iceServers\":[{\"urls\":[\"https://example.com\"],\"username\":\"u\",\"credential\":\"p\"}]}")]
    public async Task InvalidProviderResponse_PreservesStun(string json)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(json)));
        using var client = new HttpClient(handler);
        AssertStunOnly(await CreateService(client).GetAsync());
    }

    [Fact]
    public async Task NetworkFailure_PreservesStun()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("network unavailable"));
        using var client = new HttpClient(handler);
        AssertStunOnly(await CreateService(client).GetAsync());
    }

    [Fact]
    public async Task ProviderTimeout_PreservesStun()
    {
        using var handler = new StubHandler((_, _) => throw new TaskCanceledException("provider timeout"));
        using var client = new HttpClient(handler);
        AssertStunOnly(await CreateService(client).GetAsync());
    }

    [Fact]
    public async Task CallerCancellation_IsPropagated()
    {
        using var source = new CancellationTokenSource();
        using var handler = new StubHandler((_, token) =>
        {
            source.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(JsonResponse(ValidResponse));
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateService(client).GetAsync(source.Token));
    }

    private static ChatIceServersService CreateService(HttpClient client, CloudflareTurnSettings? settings = null) =>
        new(client, Options.Create(settings ?? new CloudflareTurnSettings
        {
            Enabled = true, KeyId = KeyId, ApiToken = "server-only-secret",
        }), NullLogger<ChatIceServersService>.Instance);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.Created)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static void AssertStunOnly(ChatIceServersResponse response)
    {
        var server = Assert.Single(response.IceServers);
        Assert.Equal("stun:stun.l.google.com:19302", Assert.Single(server.Urls));
        Assert.Null(server.Username);
        Assert.Null(server.Credential);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}

using BanteraApi.Admin;
using BanteraApi.Gemini;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Security.Claims;
using Xunit;
namespace BanteraApi.Tests;
public sealed class LocalModelTestTests
{
    sealed class Handler : HttpMessageHandler
    {
        public HttpRequestMessage? Sent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Sent = request; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"id\":\"test\"}") }); }
    }
    sealed class Factory(Handler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler); }
    static IConfiguration Settings(bool enabled = true) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["LocalModelTests:BaseUrl"] = enabled ? "http://private-worker:8090" : "", ["LocalModelTests:ServiceKey"] = "internal-test-secret" }).Build();
    static DefaultHttpContext Context(string method="GET")
    {
        var c = new DefaultHttpContext();c.Request.Method=method;
        c.User=new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub","00000000-0000-0000-0000-000000000001")],"test"));
        return c;
    }
    [Fact] public async Task GatewaySuppliesTrustedOwnerAndDoesNotForwardBearerOrSpoofedOwner()
    {
        var h=new Handler();var c=Context();c.Request.Headers["X-Owner-Id"]="attacker";c.Request.Headers.Authorization="Bearer private-token";
        var result=await LocalModelTestEndpoints.Forward(c,new Factory(h),Settings(),"jobs");
        Assert.Equal(202,((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal("00000000-0000-0000-0000-000000000001",h.Sent!.Headers.GetValues("X-Owner-Id").Single());
        Assert.Null(h.Sent.Headers.Authorization);Assert.Equal("private-worker",h.Sent.RequestUri!.Host);
    }
    [Fact] public async Task DisabledServiceFailsClosed()
    {
        var h=new Handler();var result=await LocalModelTestEndpoints.Forward(Context(),new Factory(h),Settings(false),"jobs");
        Assert.Equal(503,((IStatusCodeHttpResult)result).StatusCode);Assert.Null(h.Sent);
    }
    [Fact] public async Task OversizedAndWrongTypeRejectedBeforeForwarding()
    {
        var h=new Handler();var c=Context("POST");c.Request.ContentLength=LocalModelTestEndpoints.MaxBodyBytes+1;
        var result=await LocalModelTestEndpoints.Forward(c,new Factory(h),Settings(),"jobs");Assert.Equal(413,((IStatusCodeHttpResult)result).StatusCode);Assert.Null(h.Sent);
        c.Request.ContentLength=10;c.Request.ContentType="text/plain";
        result=await LocalModelTestEndpoints.Forward(c,new Factory(h),Settings(),"jobs");Assert.Equal(415,((IStatusCodeHttpResult)result).StatusCode);
    }
    [Theory]
    [InlineData("Kei te haere au ki te kura i tēnei rā.", 10)]
    [InlineData("Māori, tangata; whānau!", 3)]
    [InlineData("Ma\u0304ori", 1)]
    public void CountsMacronsAndPunctuation(string text, int count) => Assert.Equal(count, GeminiService.ModelTestWordCount(text));
    [Fact] public void RejectsWrongLengthEmptyTranslationAndControlTokens()
    {
        Assert.True(GeminiService.ValidModelTestPassage("Kei te haere au ki te kura i tēnei rā.", "I am going to school today.", 10));
        Assert.False(GeminiService.ValidModelTestPassage("Kia ora", "Hello", 10));
        Assert.False(GeminiService.ValidModelTestPassage("Kei te haere au ki te kura i tēnei rā.", "", 10));
        Assert.False(GeminiService.ValidModelTestPassage("<2en> kia ora koutou katoa", "Hello", 5));
    }
}

using System.Text;
using BanteraApi.OpenAi;
using Xunit;

namespace BanteraApi.Tests;
public class ChatGptResilienceTests
{
    [Fact]
    public async Task RecordsProviderFailureAfterSuccessfulSearchWithoutPrivateMessage()
    {
        const string stream = """
        data: {"type":"response.web_search_call.completed"}
        data: {"type":"response.content_part.added"}
        data: {"type":"response.failed","response":{"error":{"code":"server_error","message":"private prompt and credentials"}}}
        """;
        var error = await Assert.ThrowsAsync<ChatGptConnectionException>(() => Parse(stream));
        Assert.Equal("server_error", error.ProviderCode);
        Assert.True(error.SearchCompleted); Assert.Equal("answer_started", error.Stage);
        Assert.Equal(0, error.AnswerCharacters); Assert.DoesNotContain("private", error.ToString());
    }
    [Theory]
    [InlineData("rate_limit_exceeded", "quota_exceeded")]
    [InlineData("usage_limit_reached", "quota_exceeded")]
    [InlineData("invalid_request", "provider_failed")]
    public async Task DistinguishesQuotaFromOtherFailures(string provider, string expected)
    {
        var error = await Assert.ThrowsAsync<ChatGptConnectionException>(() => Parse($"data: {{\"type\":\"error\",\"error\":{{\"code\":\"{provider}\"}}}}\n"));
        Assert.Equal(expected, error.Code);
    }
    [Fact]
    public async Task RetriesTransientFailureOnceAndReturnsSuccessfulResult()
    {
        int attempts = 0;
        var result = await ChatGptSubscriptionClient.RetryTransientAsync(_ => {
            if (++attempts == 1) throw new ChatGptConnectionException("provider_failed") { ProviderCode = "server_error" };
            return Task.FromResult(new ChatGptTestResult("Done", true, []));
        }, default);
        Assert.Equal(2, attempts); Assert.True(result.SearchVerified);
        attempts = 0;
        await Assert.ThrowsAsync<ChatGptConnectionException>(() => ChatGptSubscriptionClient.RetryTransientAsync(_ => {
            attempts++; throw new ChatGptConnectionException("provider_failed") { HttpStatus = 503 };
        }, default));
        Assert.Equal(2, attempts);
    }
    [Theory]
    [InlineData("quota_exceeded", "rate_limit_exceeded", 0)]
    [InlineData("reconnect_required", "server_error", 0)]
    [InlineData("unsupported_request", "server_error", 0)]
    [InlineData("provider_failed", "server_error", 12)]
    [InlineData("provider_failed", "unknown", 0)]
    public async Task DoesNotRetryQuotaAuthInvalidRequestsOrPartialAnswers(string code, string provider, int characters)
    {
        int attempts = 0;
        await Assert.ThrowsAsync<ChatGptConnectionException>(() => ChatGptSubscriptionClient.RetryTransientAsync(_ => {
            attempts++; throw new ChatGptConnectionException(code) { ProviderCode = provider, AnswerCharacters = characters };
        }, default));
        Assert.Equal(1, attempts);
    }
    [Fact]
    public async Task OriginalDeadlineCancelsRetryDelay()
    {
        using var stop = new CancellationTokenSource(); int attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChatGptSubscriptionClient.RetryTransientAsync(_ => {
            attempts++; stop.Cancel(); throw new ChatGptConnectionException("provider_failed") { ProviderCode = "server_error" };
        }, stop.Token));
        Assert.Equal(1, attempts);
    }
    private static Task<ChatGptTestResult> Parse(string text) => ChatGptSubscriptionClient.ParseResponseAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), default);
}

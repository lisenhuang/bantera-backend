using System.Net;
using System.Text.Json;
using BanteraApi.Gemini;
using Xunit;

namespace BanteraApi.Tests;

public sealed class GeminiTestSessionTests
{
    [Fact]
    public void WrappedHttpErrorsRetainStatusAndRedaction()
    {
        var session = new GeminiTestSession("text", "tts", ["test-secret"]);
        var error = new InvalidOperationException("Model failed", new InvalidOperationException("Key failed",
            new HttpRequestException("Provider rejected test-secret", null, HttpStatusCode.BadRequest)));

        var json = session.ErrorJson(error, "tts");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(400, doc.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.Contains("Provider rejected", json);
        Assert.DoesNotContain("test-secret", json);
    }

    [Fact]
    public void ErrorsWithoutHttpResponseKeepNullStatus()
    {
        var session = new GeminiTestSession("text", "tts", []);
        using var doc = JsonDocument.Parse(session.ErrorJson(
            new InvalidOperationException("Model failed", new HttpRequestException("Network failed")), "tts"));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("httpStatus").ValueKind);
    }

    [Fact]
    public async Task DiagnosticsPreserveProviderErrorsAndRemoveSecretsAndAudio()
    {
        const string secret = "AIzaSySensitiveKeyForRedaction123456789";
        var session = new GeminiTestSession("text-model", "tts-model", [secret, "r2-secret"]);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(""),
        };
        response.Headers.Add("x-request-id", "request-123");
        response.Headers.Add("Retry-After", "60");
        response.Headers.Add("x-secret", secret);
        var payload = JsonSerializer.Serialize(new
        {
            error = new { message = $"Rejected {secret}", status = "RESOURCE_EXHAUSTED" },
            candidates = new[] { new { content = new { parts = new[] {
                new { inlineData = new { data = "base64-audio-secret", mimeType = "audio/L16" } },
            } } } },
        });
        var saves = 0;
        session.PersistAsync = () => { saves++; return Task.CompletedTask; };
        await session.CaptureAsync("tts", "tts-model", secret, new { prompt = "test" }, response, payload);

        var json = session.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, saves);
        Assert.Equal(429, doc.RootElement[0].GetProperty("httpStatus").GetInt32());
        Assert.Contains("RESOURCE_EXHAUSTED", json);
        Assert.Contains("request-123", json);
        Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain("base64-audio-secret", json);
        Assert.DoesNotContain("x-secret", json);

        var error = session.ErrorJson(new HttpRequestException(
            $"https://host?key={secret}&x=1 r2-secret Bearer aaa.bbb.ccc", null, HttpStatusCode.Forbidden), "tts");
        using var errorDoc = JsonDocument.Parse(error);
        Assert.Equal(403, errorDoc.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.DoesNotContain(secret, error);
        Assert.DoesNotContain("r2-secret", error);
        Assert.DoesNotContain("aaa.bbb.ccc", error);
    }

    [Fact]
    public async Task OversizedProviderResponsesAreExplicitlyMarkedAsTruncated()
    {
        var session = new GeminiTestSession("text", "tts", []);
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("") };
        await session.CaptureAsync("tts", "tts", "key", new { }, response, new string('x', 140_000));
        using var doc = JsonDocument.Parse(session.ToJson());
        Assert.True(doc.RootElement[0].GetProperty("responseTruncated").GetBoolean());
        Assert.Equal(128_000, doc.RootElement[0].GetProperty("responseBody").GetString()!.Length);
    }
}

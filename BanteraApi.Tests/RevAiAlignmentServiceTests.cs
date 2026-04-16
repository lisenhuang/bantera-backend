using BanteraApi.RevAi;
using System.Text.Json;
using Xunit;

namespace BanteraApi.Tests;

public class RevAiAlignmentServiceTests
{
    [Theory]
    [InlineData("en", true)]
    [InlineData("en-US", true)]
    [InlineData("en_GB", true)]
    [InlineData("fr-CA", true)]
    [InlineData("de-DE", true)]
    [InlineData("it-IT", true)]
    [InlineData("es-MX", true)]
    [InlineData("ja-JP", false)]
    [InlineData("ko-KR", false)]
    [InlineData("zh-CN", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    public void IsRevAiSupported_UsesPrimarySubtag(string languageCode, bool expected)
    {
        var actual = RevAiAlignmentService.IsRevAiSupported(languageCode);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("en_GB", "en")]
    [InlineData("fr-CA", "fr")]
    [InlineData("de-CH", "de")]
    [InlineData("it-CH", "it")]
    [InlineData("es-MX", "es")]
    [InlineData("ja-JP", null)]
    [InlineData("pt-BR", null)]
    public void TryGetSupportedLanguageCode_UsesCanonicalPrimarySubtag(
        string languageCode,
        string? expectedCanonical)
    {
        var supported = RevAiAlignmentService.TryGetSupportedLanguageCode(
            languageCode,
            out var canonical);

        Assert.Equal(expectedCanonical is not null, supported);
        Assert.Equal(expectedCanonical, canonical);
    }

    [Fact]
    public void BuildPaths_UseAlignmentApiContract()
    {
        Assert.Equal("/alignment/v1/jobs", RevAiAlignmentService.BuildSubmitPath());
        Assert.Equal("/alignment/v1/jobs/job-123", RevAiAlignmentService.BuildStatusPath("job-123"));
        Assert.Equal("/alignment/v1/jobs/job-123/transcript", RevAiAlignmentService.BuildTranscriptPath("job-123"));
    }

    [Fact]
    public void BuildSubmitBody_UsesTranscriptTextField()
    {
        var json = RevAiAlignmentService.BuildSubmitBody(
            "https://example.com/audio.wav",
            "en",
            "line one\nline two");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("https://example.com/audio.wav", root.GetProperty("source_config").GetProperty("url").GetString());
        Assert.Equal("en", root.GetProperty("language").GetString());
        Assert.Equal("line one\nline two", root.GetProperty("transcript_text").GetString());
        Assert.False(root.TryGetProperty("alignment_config", out _));
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("Completed", true)]
    [InlineData("transcribed", false)]
    [InlineData("in_progress", false)]
    [InlineData("failed", false)]
    public void IsCompletedStatus_MatchesAlignmentApiStatus(string status, bool expected)
    {
        Assert.Equal(expected, RevAiAlignmentService.IsCompletedStatus(status));
    }
}

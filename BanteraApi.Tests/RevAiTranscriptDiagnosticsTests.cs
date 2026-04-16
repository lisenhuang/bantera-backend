using BanteraApi.RevAi;
using Xunit;

namespace BanteraApi.Tests;

public class RevAiTranscriptDiagnosticsTests
{
    [Fact]
    public void BuildPreview_TruncatesAndAppendsEllipsis()
    {
        var preview = RevAiTranscriptDiagnostics.BuildPreview("abcdef", 4);
        Assert.Equal("abcd...", preview);
    }

    [Fact]
    public void BuildPreview_ReturnsOriginalWhenShort()
    {
        var preview = RevAiTranscriptDiagnostics.BuildPreview("abc", 10);
        Assert.Equal("abc", preview);
    }

    [Fact]
    public void Create_ProducesSameNormalizedHashForDifferentLineEndings()
    {
        var one = RevAiTranscriptDiagnostics.Create("A\r\nB\r\n", 100);
        var two = RevAiTranscriptDiagnostics.Create("A\nB", 100);
        Assert.Equal(one.NormalizedTranscriptHash, two.NormalizedTranscriptHash);
        Assert.NotEqual(one.TranscriptHash, two.TranscriptHash);
    }

    [Fact]
    public void CreateDelimitedJsonBlock_ContainsDelimitersAndJson()
    {
        var block = RevAiAlignmentService.CreateDelimitedJsonBlock(
            "REVAI_REQUEST_DEBUG",
            new { hello = "world" });
        Assert.Contains("===================================", block);
        Assert.Contains("REVAI_REQUEST_DEBUG", block);
        Assert.Contains("\"hello\": \"world\"", block);
    }

    [Fact]
    public void RedactSensitiveHeaderValue_RedactsBearerTokens()
    {
        var redacted = RevAiAlignmentService.RedactSensitiveHeaderValue("Bearer abc.def.ghi");
        Assert.Equal("Bearer [REDACTED]", redacted);
    }

    [Fact]
    public void CreatePossiblyTruncatedBody_MarksTruncationMetadata()
    {
        var payload = RevAiAlignmentService.CreatePossiblyTruncatedBody("abcdef", 3);
        Assert.True(payload.wasTruncated);
        Assert.Equal(6, payload.originalLength);
        Assert.Equal("abc...", payload.body);
    }

    [Fact]
    public void CreateDelimitedPlainTextBlock_PreservesLinesExactly()
    {
        var text = "sentence one\nsentence two";
        var block = RevAiAlignmentService.CreateDelimitedPlainTextBlock(text);
        Assert.Equal(
            "===================================\nbelow is the text we send to revai:\nsentence one\nsentence two\n===================================",
            block);
    }
}

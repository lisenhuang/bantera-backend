using BanteraApi.Gemini;
using BanteraApi.Videos;
using Xunit;

namespace BanteraApi.Tests;

public class RevAiCueAlignmentBuilderTests
{
    [Fact]
    public void TryBuildBoundary_UsesFirstAndLastWordsWhenMiddleWordsDiffer()
    {
        var lines = new[]
        {
            new DialogueLine("Speaker1", "I have changed my focus today"),
        };
        var wordTiming = new List<WordTimingRecord>
        {
            new("I", 0, 100, 0.99),
            new("have", 101, 220, 0.99),
            new("adjusted", 221, 420, 0.99),
            new("focus", 421, 550, 0.99),
            new("today", 551, 700, 0.99),
        };

        var success = RevAiCueAlignmentBuilder.TryBuildBoundary(lines, wordTiming, out var cues, out var failure);

        Assert.True(success);
        Assert.NotNull(cues);
        Assert.Null(failure);
        Assert.Single(cues!);
        Assert.Equal(0, cues[0].StartMs);
        Assert.Equal(700, cues[0].EndMs);
    }

    [Fact]
    public void TryBuildBoundary_FailsWhenBoundaryWordIsMissing()
    {
        var lines = new[]
        {
            new DialogueLine("Speaker1", "I have changed my focus today"),
        };
        var wordTiming = new List<WordTimingRecord>
        {
            new("I", 0, 100, 0.99),
            new("have", 101, 220, 0.99),
            new("changed", 221, 420, 0.99),
            new("focus", 421, 550, 0.99),
        };

        var success = RevAiCueAlignmentBuilder.TryBuildBoundary(lines, wordTiming, out var cues, out var failure);

        Assert.False(success);
        Assert.Null(cues);
        Assert.NotNull(failure);
        Assert.Equal("today", failure!.ExpectedToken);
    }

    [Fact]
    public void TryBuildShortCueBoundary_MergesMissingEndIntoNextCue()
    {
        var lines = new[]
        {
            new DialogueLine("Speaker1", "Hello there, nice to meet you")
            {
                ShortCues = ["Hello there", "nice to meet you"],
            },
        };
        var longCues = new[]
        {
            new VideoTranscriptCueRecord(0, 0, 900, lines[0].Text),
        };
        var wordTiming = new List<WordTimingRecord>
        {
            new("Hello", 0, 120, 0.99),
            new("nice", 300, 420, 0.99),
            new("to", 421, 500, 0.99),
            new("meet", 501, 650, 0.99),
            new("you", 651, 820, 0.99),
        };

        var success = RevAiCueAlignmentBuilder.TryBuildShortCueBoundary(lines, longCues, wordTiming, out var shortCues, out var failure);

        Assert.True(success);
        Assert.NotNull(shortCues);
        Assert.Null(failure);
        Assert.Single(shortCues!);
        Assert.Equal("Hello there nice to meet you", shortCues[0].Text);
        Assert.Equal(0, shortCues[0].StartMs);
        Assert.Equal(820, shortCues[0].EndMs);
    }

    [Fact]
    public void TryBuildShortCueBoundary_MergesMissingStartIntoPreviousCue()
    {
        var lines = new[]
        {
            new DialogueLine("Speaker1", "Hello there, nice to meet you")
            {
                ShortCues = ["Hello there", "nice to meet you"],
            },
        };
        var longCues = new[]
        {
            new VideoTranscriptCueRecord(0, 0, 900, lines[0].Text),
        };
        var wordTiming = new List<WordTimingRecord>
        {
            new("Hello", 0, 120, 0.99),
            new("there", 121, 260, 0.99),
            new("to", 421, 500, 0.99),
            new("meet", 501, 650, 0.99),
            new("you", 651, 820, 0.99),
        };

        var success = RevAiCueAlignmentBuilder.TryBuildShortCueBoundary(lines, longCues, wordTiming, out var shortCues, out var failure);

        Assert.True(success);
        Assert.NotNull(shortCues);
        Assert.Null(failure);
        Assert.Single(shortCues!);
        Assert.Equal("Hello there nice to meet you", shortCues[0].Text);
        Assert.Equal(0, shortCues[0].StartMs);
        Assert.Equal(820, shortCues[0].EndMs);
    }

    [Fact]
    public void TryBuild_AlignsCueBoundsToFirstAndLastMatchedWords()
    {
        var lines = new[]
        {
            new DialogueLine("Speaker1", "Man, this line is wrapped around the building today! I guess everyone had the same idea."),
            new DialogueLine("Speaker2", "Nice to meet you, Mike. I'm Sarah."),
        };

        var wordTiming = new List<WordTimingRecord>
        {
            new("Man", 0, 200, 0.99),
            new("this", 201, 320, 0.99),
            new("line", 321, 420, 0.99),
            new("is", 421, 510, 0.99),
            new("wrapped", 511, 770, 0.99),
            new("around", 771, 980, 0.99),
            new("the", 981, 1090, 0.99),
            new("building", 1091, 1420, 0.99),
            new("today", 1421, 1640, 0.99),
            new("I", 1641, 1710, 0.99),
            new("guess", 1711, 1950, 0.99),
            new("everyone", 1951, 2240, 0.99),
            new("had", 2241, 2370, 0.99),
            new("the", 2371, 2470, 0.99),
            new("same", 2471, 2690, 0.99),
            new("idea", 4765, 5055, 0.99),
            new("Nice", 12365, 12655, 0.99),
            new("to", 12656, 12780, 0.99),
            new("meet", 12781, 13020, 0.99),
            new("you", 13021, 13210, 0.99),
            new("Mike", 13211, 13480, 0.99),
            new("I'm", 13481, 13730, 0.99),
            new("Sarah", 13731, 14020, 0.99),
        };

        var success = RevAiCueAlignmentBuilder.TryBuild(lines, wordTiming, out var cues, out var failure);

        Assert.True(success);
        Assert.NotNull(cues);
        Assert.Null(failure);
        Assert.Equal(2, cues!.Count);
        Assert.Equal(0, cues[0].StartMs);
        Assert.Equal(5055, cues[0].EndMs);
        Assert.Equal(12365, cues[1].StartMs);
        Assert.Equal(14020, cues[1].EndMs);
    }

    [Fact]
    public void TryBuild_ReturnsFailureWhenLineCannotBeMatched()
    {
        var lines = new[]
        {
            new DialogueLine("Speaker1", "Hello there."),
            new DialogueLine("Speaker2", "Completely missing line."),
        };
        var wordTiming = new List<WordTimingRecord>
        {
            new("Hello", 0, 200, 0.99),
            new("there", 201, 500, 0.99),
        };

        var success = RevAiCueAlignmentBuilder.TryBuild(lines, wordTiming, out var cues, out var failure);

        Assert.False(success);
        Assert.Null(cues);
        Assert.NotNull(failure);
        Assert.Equal(1, failure!.LineIndex);
        Assert.True(failure.ExpectedWords > 0);
    }

    [Fact]
    public void TryBuild_IgnoresPunctuationDifferences()
    {
        var lines = new[]
        {
            new DialogueLine("Speaker1", "Hello, David!"),
            new DialogueLine("Speaker2", "Nice to meet you."),
        };
        var wordTiming = new List<WordTimingRecord>
        {
            new("Hello", 0, 120, 0.99),
            new("David", 121, 300, 0.99),
            new("Nice", 301, 420, 0.99),
            new("to", 421, 490, 0.99),
            new("meet", 491, 620, 0.99),
            new("you", 621, 760, 0.99),
        };

        var success = RevAiCueAlignmentBuilder.TryBuild(lines, wordTiming, out var cues, out var failure);

        Assert.True(success);
        Assert.NotNull(cues);
        Assert.Null(failure);
        Assert.Equal(2, cues!.Count);
        Assert.Equal(0, cues[0].StartMs);
        Assert.Equal(300, cues[0].EndMs);
    }

    [Fact]
    public void TryBuild_ToleratesSingleTokenMismatchWhenMatchRatioIsHigh()
    {
        var lines = new[]
        {
            new DialogueLine("Speaker1", "I have changed my focus today"),
        };
        var wordTiming = new List<WordTimingRecord>
        {
            new("I", 0, 100, 0.99),
            new("have", 101, 220, 0.99),
            new("changed", 221, 420, 0.99),
            new("focus", 421, 550, 0.99),
            new("today", 551, 700, 0.99),
        };

        var success = RevAiCueAlignmentBuilder.TryBuild(lines, wordTiming, out var cues, out var failure);

        Assert.True(success);
        Assert.NotNull(cues);
        Assert.Null(failure);
        Assert.Single(cues!);
        Assert.Equal(0, cues[0].StartMs);
        Assert.Equal(700, cues[0].EndMs);
    }

    [Fact]
    public void TryBuild_FailsWhenMatchQualityIsTooLow()
    {
        var lines = new[]
        {
            new DialogueLine("Speaker1", "one two three four five six"),
        };
        var wordTiming = new List<WordTimingRecord>
        {
            new("one", 0, 100, 0.99),
            new("two", 101, 220, 0.99),
        };

        var success = RevAiCueAlignmentBuilder.TryBuild(lines, wordTiming, out var cues, out var failure);

        Assert.False(success);
        Assert.Null(cues);
        Assert.NotNull(failure);
        Assert.True(failure!.MatchRatio < failure.RequiredMatchRatio);
        Assert.Equal(2, failure.MatchedWords);
        Assert.Equal(6, failure.ExpectedWords);
    }

    [Fact]
    public void TryBuild_UsesFirstMatchedWordStartEvenWhenEarlierThanPreviousCueEnd()
    {
        var lines = new[]
        {
            new DialogueLine("Speaker1", "First cue line"),
            new DialogueLine("Speaker2", "I've gotten unique hands on experience"),
        };
        var wordTiming = new List<WordTimingRecord>
        {
            new("First", 0, 2000, 0.99),
            new("cue", 2001, 4000, 0.99),
            new("line", 4001, 5610, 0.99),
            new("I've", 5340, 5610, 0.99),
            new("gotten", 5611, 6800, 0.99),
            new("unique", 6801, 7600, 0.99),
            new("hands", 7601, 8500, 0.99),
            new("on", 8501, 9000, 0.99),
            new("experience", 9001, 11940, 0.99),
        };

        var success = RevAiCueAlignmentBuilder.TryBuild(lines, wordTiming, out var cues, out var failure);

        Assert.True(success);
        Assert.NotNull(cues);
        Assert.Null(failure);
        Assert.Equal(2, cues!.Count);
        Assert.Equal(5610, cues[0].EndMs);
        Assert.Equal(5340, cues[1].StartMs);
        Assert.Equal(11940, cues[1].EndMs);
    }

    [Fact]
    public void TryBuild_AnchorsCueStartToThatsWordTiming()
    {
        var lines = new[]
        {
            new DialogueLine("Speaker1", "Earlier cue line"),
            new DialogueLine("Speaker2", "That’s so nice of you Mike"),
        };
        var wordTiming = new List<WordTimingRecord>
        {
            new("Earlier", 0, 8000, 0.99),
            new("cue", 8001, 12000, 0.99),
            new("line", 12001, 18000, 0.99),
            new("That’s", 17730, 18000, 0.99),
            new("so", 18001, 18700, 0.99),
            new("nice", 18701, 19600, 0.99),
            new("of", 19601, 20100, 0.99),
            new("you", 20101, 20800, 0.99),
            new("Mike", 20801, 22800, 0.99),
        };

        var success = RevAiCueAlignmentBuilder.TryBuild(lines, wordTiming, out var cues, out var failure);

        Assert.True(success);
        Assert.NotNull(cues);
        Assert.Null(failure);
        Assert.Equal(2, cues!.Count);
        Assert.Equal(17730, cues[1].StartMs);
        Assert.Equal(22800, cues[1].EndMs);
    }

    [Fact]
    public void TryBuild_NormalizesSmartAndAsciiApostrophesForFirstWordAnchor()
    {
        var lines = new[]
        {
            new DialogueLine("Speaker1", "Earlier cue line"),
            new DialogueLine("Speaker2", "That's so nice of you Mike"),
        };
        var wordTiming = new List<WordTimingRecord>
        {
            new("Earlier", 0, 8000, 0.99),
            new("cue", 8001, 12000, 0.99),
            new("line", 12001, 18000, 0.99),
            new("That’s", 17730, 18000, 0.99),
            new("so", 18001, 18700, 0.99),
            new("nice", 18701, 19600, 0.99),
            new("of", 19601, 20100, 0.99),
            new("you", 20101, 20800, 0.99),
            new("Mike", 20801, 22800, 0.99),
        };

        var success = RevAiCueAlignmentBuilder.TryBuild(lines, wordTiming, out var cues, out var failure);

        Assert.True(success);
        Assert.NotNull(cues);
        Assert.Null(failure);
        Assert.Equal(17730, cues![1].StartMs);
    }
}

using BanteraApi.Mcp;
using ModelContextProtocol;
using Xunit;

namespace BanteraApi.Tests;

public class McpPeriodTests
{
    [Fact]
    public void Parse_RelativeDaysCoversThatManyDaysIncludingToday()
    {
        var period = McpPeriod.Parse("7d");
        Assert.Equal(7, period.TotalDays);
    }

    [Theory]
    [InlineData("2w", 14)]
    [InlineData("12w", 84)]
    public void Parse_RelativeWeeks(string input, int expectedDays)
    {
        Assert.Equal(expectedDays, McpPeriod.Parse(input).TotalDays);
    }

    [Fact]
    public void Parse_RelativeMonthsUsesCalendarMonths()
    {
        var period = McpPeriod.Parse("1m");
        Assert.InRange(period.TotalDays, 28, 31);
    }

    [Fact]
    public void Parse_TodayAndYesterdayAreSingleDays()
    {
        Assert.Equal(1, McpPeriod.Parse("today").TotalDays);
        Assert.Equal(1, McpPeriod.Parse("yesterday").TotalDays);
    }

    [Fact]
    public void Parse_SingleDateIsOneDay()
    {
        var period = McpPeriod.Parse("2026-01-15");

        Assert.Equal(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), period.FromUtc);
        Assert.Equal(new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc), period.ToUtc);
    }

    [Fact]
    public void Parse_RangeEndIsInclusive()
    {
        var period = McpPeriod.Parse("2026-01-01..2026-01-31");

        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), period.FromUtc);
        // 31 January is included, so the exclusive end is 1 February.
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), period.ToUtc);
        Assert.Equal(31, period.TotalDays);
    }

    [Fact]
    public void Parse_UsesFallbackWhenBlank()
    {
        Assert.Equal(McpPeriod.Parse("30d").TotalDays, McpPeriod.Parse(null).TotalDays);
        Assert.Equal(7, McpPeriod.Parse("", "7d").TotalDays);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("0d")]
    [InlineData("-5d")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-01..2026-01-01")]
    public void Parse_RejectsInvalidInput(string input)
    {
        Assert.Throws<McpException>(() => McpPeriod.Parse(input));
    }

    [Fact]
    public void Parse_RejectsWindowsLongerThanTwoYears()
    {
        Assert.Throws<McpException>(() => McpPeriod.Parse("800d"));
    }

    [Theory]
    [InlineData("30d", McpBuckets.Day)]
    [InlineData("62d", McpBuckets.Day)]
    [InlineData("90d", McpBuckets.Week)]
    [InlineData("500d", McpBuckets.Month)]
    public void AutoBucket_ScalesWithWindowLength(string period, string expected)
    {
        Assert.Equal(expected, McpPeriod.Parse(period).AutoBucket());
    }

    [Fact]
    public void ValidateBucket_AcceptsWhitelistAndAuto()
    {
        var period = McpPeriod.Parse("30d");

        Assert.Equal(McpBuckets.Day, McpBuckets.Validate("day", period));
        Assert.Equal(McpBuckets.Week, McpBuckets.Validate("WEEK", period));
        Assert.Equal(McpBuckets.Day, McpBuckets.Validate("auto", period));
        Assert.Equal(McpBuckets.Day, McpBuckets.Validate(null, period));
    }

    [Fact]
    public void ValidateBucket_RejectsAnythingElse()
    {
        var period = McpPeriod.Parse("30d");

        // This value is interpolated into SQL as a literal, so the whitelist is the
        // injection boundary and must reject everything unexpected.
        Assert.Throws<McpException>(() => McpBuckets.Validate("day'; DROP TABLE users;--", period));
        Assert.Throws<McpException>(() => McpBuckets.Validate("hour", period));
    }

    [Fact]
    public void Paging_ClampsToBounds()
    {
        Assert.Equal(1, McpPaging.Clamp(0));
        Assert.Equal(100, McpPaging.Clamp(5000));
        Assert.Equal(50, McpPaging.Clamp(50));
        Assert.Equal(0, McpPaging.ClampOffset(-10));
    }
}

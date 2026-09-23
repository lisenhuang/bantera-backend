using System.Globalization;
using ModelContextProtocol;

namespace BanteraApi.Mcp;

/// <summary>A resolved UTC time window. <see cref="ToUtc"/> is exclusive.</summary>
public sealed record McpPeriod(DateTime FromUtc, DateTime ToUtc, string Label)
{
    public int TotalDays => (int)Math.Ceiling((ToUtc - FromUtc).TotalDays);

    /// <summary>
    /// Parses the period syntax exposed to the model: "7d", "12w", "6m", "today",
    /// "yesterday", "YYYY-MM-DD", or "YYYY-MM-DD..YYYY-MM-DD" (end inclusive).
    /// </summary>
    public static McpPeriod Parse(string? value, string fallback = "30d")
    {
        var raw = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var now = DateTime.UtcNow;
        var todayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        var tomorrow = todayStart.AddDays(1);

        McpPeriod result = raw.ToLowerInvariant() switch
        {
            "today" => new McpPeriod(todayStart, tomorrow, "today"),
            "yesterday" => new McpPeriod(todayStart.AddDays(-1), todayStart, "yesterday"),
            _ => ParseComplex(raw, todayStart, tomorrow),
        };

        if (result.FromUtc >= result.ToUtc)
            throw new McpException("Invalid period: the start must be before the end.");

        if (result.TotalDays > 730)
            throw new McpException("Invalid period: at most 730 days can be queried at once.");

        return result;
    }

    private static McpPeriod ParseComplex(string raw, DateTime todayStart, DateTime tomorrow)
    {
        // Explicit range: 2026-01-01..2026-02-01 (end inclusive)
        var rangeSeparator = raw.IndexOf("..", StringComparison.Ordinal);
        if (rangeSeparator > 0)
        {
            var fromPart = raw[..rangeSeparator].Trim();
            var toPart = raw[(rangeSeparator + 2)..].Trim();

            if (TryParseDate(fromPart, out var from) && TryParseDate(toPart, out var to))
                return new McpPeriod(from, to.AddDays(1), raw);

            throw InvalidPeriod();
        }

        // Single day: 2026-01-01
        if (TryParseDate(raw, out var single))
            return new McpPeriod(single, single.AddDays(1), raw);

        // Relative: 7d / 12w / 6m
        if (raw.Length >= 2)
        {
            var unit = char.ToLowerInvariant(raw[^1]);
            if (int.TryParse(raw[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
            {
                var from = unit switch
                {
                    'd' => tomorrow.AddDays(-n),
                    'w' => tomorrow.AddDays(-7 * n),
                    'm' => tomorrow.AddMonths(-n),
                    _ => DateTime.MinValue,
                };

                if (from != DateTime.MinValue)
                    return new McpPeriod(from, tomorrow, raw);
            }
        }

        throw InvalidPeriod();
    }

    private static bool TryParseDate(string value, out DateTime utcMidnight)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            utcMidnight = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            return true;
        }

        utcMidnight = default;
        return false;
    }

    private static McpException InvalidPeriod()
        => new("Invalid period. Use '7d', '12w', '6m', 'today', 'yesterday', '2026-01-01', or '2026-01-01..2026-02-01'.");

    /// <summary>Chooses a sensible bucket size for the window length.</summary>
    public string AutoBucket() => TotalDays switch
    {
        <= 62 => McpBuckets.Day,
        <= 400 => McpBuckets.Week,
        _ => McpBuckets.Month,
    };
}

/// <summary>
/// Whitelisted bucket units. These are the only values ever interpolated into SQL as
/// literals, so the whitelist is the injection boundary.
/// </summary>
public static class McpBuckets
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";

    public static string Validate(string? value, McpPeriod period)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return period.AutoBucket();

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            Day or Week or Month => normalized,
            _ => throw new McpException("Invalid bucket. Use 'day', 'week', 'month' or 'auto'."),
        };
    }

    /// <summary>The Postgres interval literal for a bucket unit.</summary>
    public static string Interval(string bucket) => bucket switch
    {
        Day => "1 day",
        Week => "1 week",
        Month => "1 month",
        _ => "1 day",
    };
}

public static class McpPaging
{
    public static int Clamp(int value, int max = 100) => Math.Clamp(value, 1, max);
    public static int ClampOffset(int value) => Math.Max(value, 0);
}

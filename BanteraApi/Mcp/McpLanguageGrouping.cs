using BanteraApi.Chat;

namespace BanteraApi.Mcp;

public sealed record LanguageVariant(string Code, int Users);

public sealed record LanguageGroup(
    string Key,
    string DisplayName,
    int Users,
    double Pct,
    IReadOnlyList<LanguageVariant> Variants);

/// <summary>
/// Groups the free-text language columns for reporting.
///
/// The columns are not case-normalised in the database ("en-US", "en-us" and "EN-us" can all
/// be present), and regional variants are usually noise for an analytics question, so counts
/// are grouped by language family via <see cref="ChatLanguageResolver"/> ("en-US" and "en-GB"
/// both become "en"; "zh-HK" and "zh-TW" stay distinct). Grouping happens in memory, after a
/// cheap SQL GROUP BY on the raw column.
/// </summary>
public static class McpLanguageGrouping
{
    public const string UnsetKey = "unset";

    public static IReadOnlyList<LanguageGroup> Group(
        IEnumerable<(string? Code, int Count)> rows,
        bool byFamily = true)
    {
        var buckets = new Dictionary<string, (string Display, int Total, Dictionary<string, int> Variants)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (code, count) in rows)
        {
            var descriptor = ChatLanguageResolver.Resolve(code);

            string key, display, variantCode;
            if (descriptor is null)
            {
                key = UnsetKey;
                display = "Not set";
                variantCode = UnsetKey;
            }
            else if (byFamily)
            {
                key = descriptor.MatchKey;
                display = descriptor.DisplayName;
                variantCode = descriptor.OriginalCode;
            }
            else
            {
                key = descriptor.OriginalCode;
                display = descriptor.ExactDisplayName;
                variantCode = descriptor.OriginalCode;
            }

            if (!buckets.TryGetValue(key, out var bucket))
                bucket = (display, 0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

            bucket.Total += count;
            bucket.Variants[variantCode] = bucket.Variants.GetValueOrDefault(variantCode) + count;
            buckets[key] = bucket;
        }

        var grandTotal = buckets.Values.Sum(b => b.Total);

        return [.. buckets
            .Select(kv => new LanguageGroup(
                kv.Key,
                kv.Value.Display,
                kv.Value.Total,
                grandTotal == 0 ? 0 : Math.Round(kv.Value.Total * 100.0 / grandTotal, 1),
                [.. kv.Value.Variants
                    .Select(v => new LanguageVariant(v.Key, v.Value))
                    .OrderByDescending(v => v.Users)]))
            .OrderByDescending(g => g.Users)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Normalizes a caller-supplied language filter to a family key, so "en-GB", "en" and
    /// "EN" all select the same users.
    /// </summary>
    public static string? ToFamilyKey(string? input)
        => ChatLanguageResolver.Resolve(input)?.MatchKey;

    /// <summary>
    /// True when a stored code belongs to the given family key. Applied in memory or as a
    /// client-side filter; SQL-side filtering uses a LIKE on the lowered column.
    /// </summary>
    public static bool IsInFamily(string? storedCode, string familyKey)
        => ChatLanguageResolver.Resolve(storedCode)?.MatchKey.Equals(familyKey, StringComparison.OrdinalIgnoreCase) == true;
}

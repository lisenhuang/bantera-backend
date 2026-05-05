using System.Globalization;

namespace BanteraApi.Chat;

public sealed record ChatLanguageDescriptor(
    string OriginalCode,
    string MatchKey,
    string DisplayName,
    string ExactDisplayName
);

public static class ChatLanguageResolver
{
    private static readonly Lazy<Dictionary<string, LearningLanguageItem>> CatalogByIdentifier =
        new(() => LearningLanguageCatalog.Items
            .Concat(TranslationLanguageCatalog.Items)
            .GroupBy(item => Normalize(item.Identifier) ?? item.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase));
    private static readonly Lazy<HashSet<string>> LearningCatalogMatchKeys =
        new(() => LearningLanguageCatalog.Items
            .Select(item => Normalize(item.Identifier))
            .Where(normalized => normalized is not null)
            .Select(normalized => MatchKeyFor(normalized!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    public static ChatLanguageDescriptor? Resolve(string? languageCode)
    {
        var normalized = Normalize(languageCode);
        if (normalized is null)
            return null;

        var catalog = CatalogByIdentifier.Value;
        var exactDisplay = catalog.TryGetValue(normalized, out var exactItem)
            ? exactItem.DisplayName
            : ToFallbackDisplayName(ToCanonical(normalized));

        var matchKey = MatchKeyFor(normalized);
        var displayName = catalog.TryGetValue(matchKey, out var groupItem)
            ? groupItem.DisplayName
            : ToFallbackDisplayName(ToCanonical(matchKey));

        return new ChatLanguageDescriptor(
            ToCanonical(normalized),
            matchKey,
            displayName,
            exactDisplay);
    }

    public static string? Normalize(string? languageCode)
    {
        var normalized = (languageCode ?? string.Empty).Trim().Replace('_', '-');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (string.Equals(normalized, "zh", StringComparison.OrdinalIgnoreCase))
            return "zh";

        return normalized.ToLowerInvariant();
    }

    public static bool MatchesAny(string? languageCode, IEnumerable<string> matchKeys)
    {
        var descriptor = Resolve(languageCode);
        if (descriptor is null)
            return false;

        return matchKeys.Contains(descriptor.MatchKey, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsLearningCatalogLanguageFamily(string? languageCode)
    {
        var normalized = Normalize(languageCode);
        if (normalized is null)
            return false;

        return LearningCatalogMatchKeys.Value.Contains(MatchKeyFor(normalized));
    }

    private static string MatchKeyFor(string normalized)
    {
        if (normalized.StartsWith("zh-", StringComparison.Ordinal))
            return normalized;

        return normalized.Split('-', 2)[0];
    }

    private static string ToCanonical(string normalized)
    {
        var parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return normalized;

        if (parts.Length == 1)
            return parts[0].ToLowerInvariant();

        return $"{parts[0].ToLowerInvariant()}-{parts[1].ToUpperInvariant()}";
    }

    private static string ToFallbackDisplayName(string canonical)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(canonical);
            return culture.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            var primary = canonical.Split('-', 2)[0];
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(primary.ToLowerInvariant());
        }
    }
}

using BanteraApi.Chat;

namespace BanteraApi;

/// <summary>
/// Flag emoji for a language code, taken from the curated catalogs so a language always
/// shows the same flag everywhere in the product.
///
/// Lookup order: the exact code ("en-NZ"), then its language family ("en"), then the region
/// subtag turned into a flag ("xx-BR" → 🇧🇷), and finally a neutral globe.
/// </summary>
public static class LanguageFlags
{
    public const string Globe = "🌐";

    private static readonly Lazy<Dictionary<string, string>> ByCode = new(() =>
        TranslationLanguageCatalog.Items
            .Concat(LearningLanguageCatalog.Items)
            .GroupBy(i => i.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().FlagEmoji, StringComparer.OrdinalIgnoreCase));

    public static string For(string? languageCode)
    {
        var normalized = ChatLanguageResolver.Normalize(languageCode);
        if (normalized is null) return Globe;

        if (ByCode.Value.TryGetValue(normalized, out var exact))
            return exact;

        var family = ChatLanguageResolver.Resolve(normalized)?.MatchKey;
        if (family is not null && ByCode.Value.TryGetValue(family, out var familyFlag))
            return familyFlag;

        var parts = normalized.Split('-');
        if (parts.Length > 1 && FromCountryCode(parts[^1]) is { } regionFlag)
            return regionFlag;

        return Globe;
    }

    /// <summary>Builds a flag from an ISO 3166-1 alpha-2 code using regional indicator symbols.</summary>
    public static string? FromCountryCode(string? countryCode)
    {
        if (countryCode is not { Length: 2 }) return null;

        var upper = countryCode.ToUpperInvariant();
        if (!char.IsAsciiLetterUpper(upper[0]) || !char.IsAsciiLetterUpper(upper[1])) return null;

        const int regionalIndicatorA = 0x1F1E6;
        return char.ConvertFromUtf32(regionalIndicatorA + (upper[0] - 'A'))
             + char.ConvertFromUtf32(regionalIndicatorA + (upper[1] - 'A'));
    }
}

namespace BanteraApi;

/// <summary>
/// Curated learning language catalog: English variants plus French, Italian, German, and Spanish variants.
/// </summary>
public sealed record LearningLanguageItem(
    string Identifier,
    string DisplayName,
    string FlagEmoji);

public static class LearningLanguageCatalog
{
    /// <summary>Curated global popularity order, using region-specific identifiers.</summary>
    public static IReadOnlyList<LearningLanguageItem> Items { get; } =
    [
        // English (13)
        new("en-US", "English (United States)", "🇺🇸"),
        new("en-GB", "English (United Kingdom)", "🇬🇧"),
        new("en-AU", "English (Australia)", "🇦🇺"),
        new("en-CA", "English (Canada)", "🇨🇦"),
        new("en-IN", "English (India)", "🇮🇳"),
        new("en-NZ", "English (New Zealand)", "🇳🇿"),
        new("en-IE", "English (Ireland)", "🇮🇪"),
        new("en-SG", "English (Singapore)", "🇸🇬"),
        new("en-ZA", "English (South Africa)", "🇿🇦"),
        new("en-PH", "English (Philippines)", "🇵🇭"),
        new("en-AE", "English (United Arab Emirates)", "🇦🇪"),
        new("en-ID", "English (Indonesia)", "🇮🇩"),
        new("en-SA", "English (Saudi Arabia)", "🇸🇦"),
        // Spanish (6)
        new("es-MX", "Spanish (Mexico)", "🇲🇽"),
        new("es-ES", "Spanish (Spain)", "🇪🇸"),
        new("es-419", "Spanish (Latin America)", "🌎"),
        new("es-US", "Spanish (United States)", "🇺🇸"),
        new("es-CO", "Spanish (Colombia)", "🇨🇴"),
        new("es-CL", "Spanish (Chile)", "🇨🇱"),
        // French (4)
        new("fr-FR", "French (France)", "🇫🇷"),
        new("fr-CA", "French (Canada)", "🇨🇦"),
        new("fr-BE", "French (Belgium)", "🇧🇪"),
        new("fr-CH", "French (Switzerland)", "🇨🇭"),
        // German (3)
        new("de-DE", "German (Germany)", "🇩🇪"),
        new("de-AT", "German (Austria)", "🇦🇹"),
        new("de-CH", "German (Switzerland)", "🇨🇭"),
        // Italian (2)
        new("it-IT", "Italian (Italy)", "🇮🇹"),
        new("it-CH", "Italian (Switzerland)", "🇨🇭"),
    ];
}

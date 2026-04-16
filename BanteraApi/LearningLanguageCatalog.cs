namespace BanteraApi;

/// <summary>
/// Curated learning language catalog: English variants plus French, Italian, German, and Spanish.
/// </summary>
public sealed record LearningLanguageItem(
    string Identifier,
    string DisplayName,
    string FlagEmoji);

public static class LearningLanguageCatalog
{
    /// <summary>Deterministic order: generic language codes precede region variants.</summary>
    public static IReadOnlyList<LearningLanguageItem> Items { get; } =
    [
        // English (13)
        new("en-AU", "English (Australia)", "🇦🇺"),
        new("en-CA", "English (Canada)", "🇨🇦"),
        new("en-ID", "English (Indonesia)", "🇮🇩"),
        new("en-IE", "English (Ireland)", "🇮🇪"),
        new("en-IN", "English (India)", "🇮🇳"),
        new("en-NZ", "English (New Zealand)", "🇳🇿"),
        new("en-PH", "English (Philippines)", "🇵🇭"),
        new("en-SA", "English (Saudi Arabia)", "🇸🇦"),
        new("en-SG", "English (Singapore)", "🇸🇬"),
        new("en-ZA", "English (South Africa)", "🇿🇦"),
        new("en-AE", "English (United Arab Emirates)", "🇦🇪"),
        new("en-GB", "English (United Kingdom)", "🇬🇧"),
        new("en-US", "English (United States)", "🇺🇸"),
        // French (5)
        new("fr", "French", "🇫🇷"),
        new("fr-BE", "French (Belgium)", "🇧🇪"),
        new("fr-CA", "French (Canada)", "🇨🇦"),
        new("fr-CH", "French (Switzerland)", "🇨🇭"),
        new("fr-FR", "French (France)", "🇫🇷"),
        // Italian (3)
        new("it", "Italian", "🇮🇹"),
        new("it-CH", "Italian (Switzerland)", "🇨🇭"),
        new("it-IT", "Italian (Italy)", "🇮🇹"),
        // German (4)
        new("de", "German", "🇩🇪"),
        new("de-AT", "German (Austria)", "🇦🇹"),
        new("de-CH", "German (Switzerland)", "🇨🇭"),
        new("de-DE", "German (Germany)", "🇩🇪"),
        // Spanish (7)
        new("es", "Spanish", "🇪🇸"),
        new("es-419", "Spanish (Latin America)", "🌎"),
        new("es-CL", "Spanish (Chile)", "🇨🇱"),
        new("es-CO", "Spanish (Colombia)", "🇨🇴"),
        new("es-ES", "Spanish (Spain)", "🇪🇸"),
        new("es-MX", "Spanish (Mexico)", "🇲🇽"),
        new("es-US", "Spanish (United States)", "🇺🇸"),
    ];
}

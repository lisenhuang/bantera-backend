namespace BanteraApi;

/// <summary>
/// iOS built-in translation locale catalog (LanguageAvailability.supportedLanguages).
/// </summary>
public static class TranslationLanguageCatalog
{
    /// <summary>Deterministic order matching the iOS built-in translation supported languages.</summary>
    public static IReadOnlyList<LearningLanguageItem> Items { get; } =
    [
        new("ar-AE", "Arabic (United Arab Emirates)", "🇦🇪"),
        new("zh",    "Chinese",                       "🇨🇳"),
        new("zh-HK", "Chinese (Hong Kong)",           "🇭🇰"),
        new("zh-TW", "Chinese (Taiwan)",              "🇹🇼"),
        new("da",    "Danish",                        "🇩🇰"),
        new("nl",    "Dutch",                         "🇳🇱"),
        new("en",    "English",                       "🇬🇧"),
        new("en-AU", "English (Australia)",           "🇦🇺"),
        new("en-CA", "English (Canada)",              "🇨🇦"),
        new("en-IN", "English (India)",               "🇮🇳"),
        new("en-IE", "English (Ireland)",             "🇮🇪"),
        new("en-NZ", "English (New Zealand)",         "🇳🇿"),
        new("en-SG", "English (Singapore)",           "🇸🇬"),
        new("en-ZA", "English (South Africa)",        "🇿🇦"),
        new("en-GB", "English (United Kingdom)",      "🇬🇧"),
        new("fr",    "French",                        "🇫🇷"),
        new("fr-CA", "French (Canada)",               "🇨🇦"),
        new("de",    "German",                        "🇩🇪"),
        new("de-CH", "German (Switzerland)",          "🇨🇭"),
        new("hi",    "Hindi",                         "🇮🇳"),
        new("id",    "Indonesian",                    "🇮🇩"),
        new("it",    "Italian",                       "🇮🇹"),
        new("it-CH", "Italian (Switzerland)",         "🇨🇭"),
        new("ja",    "Japanese",                      "🇯🇵"),
        new("ko",    "Korean",                        "🇰🇷"),
        new("nb",    "Norwegian Bokmål",              "🇳🇴"),
        new("pl",    "Polish",                        "🇵🇱"),
        new("pt",    "Portuguese",                    "🇧🇷"),
        new("pt-PT", "Portuguese (Portugal)",         "🇵🇹"),
        new("ru",    "Russian",                       "🇷🇺"),
        new("es",    "Spanish",                       "🇪🇸"),
        new("es-MX", "Spanish (Mexico)",              "🇲🇽"),
        new("es-US", "Spanish (United States)",       "🇺🇸"),
        new("sv",    "Swedish",                       "🇸🇪"),
        new("th",    "Thai",                          "🇹🇭"),
        new("tr",    "Turkish",                       "🇹🇷"),
        new("uk",    "Ukrainian",                     "🇺🇦"),
        new("vi",    "Vietnamese",                    "🇻🇳"),
    ];
}

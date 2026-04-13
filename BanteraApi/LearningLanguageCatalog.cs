namespace BanteraApi;

/// <summary>
/// Curated learning / transcription locale catalog aligned with Gemini locale keys.
/// </summary>
public sealed record LearningLanguageItem(
    string Identifier,
    string DisplayName,
    string FlagEmoji);

public static class LearningLanguageCatalog
{
    /// <summary>Deterministic order matching the supported transcription locales.</summary>
    public static IReadOnlyList<LearningLanguageItem> Items { get; } =
    [
        new("yue-CN", "Cantonese (China mainland)", "🇨🇳"),
        new("zh-CN", "Chinese (China mainland)", "🇨🇳"),
        new("zh-HK", "Chinese (Hong Kong)", "🇭🇰"),
        new("zh-TW", "Chinese (Taiwan)", "🇹🇼"),
        new("en-AU", "English (Australia)", "🇦🇺"),
        new("en-CA", "English (Canada)", "🇨🇦"),
        new("en-IN", "English (India)", "🇮🇳"),
        new("en-IE", "English (Ireland)", "🇮🇪"),
        new("en-NZ", "English (New Zealand)", "🇳🇿"),
        new("en-SG", "English (Singapore)", "🇸🇬"),
        new("en-ZA", "English (South Africa)", "🇿🇦"),
        new("en-GB", "English (United Kingdom)", "🇬🇧"),
        new("en-US", "English (United States)", "🇺🇸"),
        new("fr-BE", "French (Belgium)", "🇧🇪"),
        new("fr-CA", "French (Canada)", "🇨🇦"),
        new("fr-FR", "French (France)", "🇫🇷"),
        new("fr-CH", "French (Switzerland)", "🇨🇭"),
        new("de-AT", "German (Austria)", "🇦🇹"),
        new("de-DE", "German (Germany)", "🇩🇪"),
        new("de-CH", "German (Switzerland)", "🇨🇭"),
        new("it-IT", "Italian (Italy)", "🇮🇹"),
        new("it-CH", "Italian (Switzerland)", "🇨🇭"),
        new("ja-JP", "Japanese (Japan)", "🇯🇵"),
        new("ko-KR", "Korean (South Korea)", "🇰🇷"),
        new("pt-BR", "Portuguese (Brazil)", "🇧🇷"),
        new("pt-PT", "Portuguese (Portugal)", "🇵🇹"),
        new("es-CL", "Spanish (Chile)", "🇨🇱"),
        new("es-MX", "Spanish (Mexico)", "🇲🇽"),
        new("es-ES", "Spanish (Spain)", "🇪🇸"),
        new("es-US", "Spanish (United States)", "🇺🇸"),
    ];
}

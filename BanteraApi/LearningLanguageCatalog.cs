namespace BanteraApi;

/// <summary>
/// Learning language catalog. Matches the native-language list: every speech-recognition locale
/// (AI audio uses Gemini TTS + Gemini Transcribe, which cover all of them), region-specific.
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
        // Chinese (4)
        new("zh-CN", "Chinese, Mandarin (China mainland)", "🇨🇳"),
        new("zh-TW", "Chinese, Mandarin (Taiwan)", "🇹🇼"),
        new("zh-HK", "Cantonese (Hong Kong)", "🇭🇰"),
        new("yue-CN", "Cantonese (China mainland)", "🇨🇳"),
        // Japanese, Korean
        new("ja-JP", "Japanese (Japan)", "🇯🇵"),
        new("ko-KR", "Korean (South Korea)", "🇰🇷"),
        // Portuguese (2)
        new("pt-BR", "Portuguese (Brazil)", "🇧🇷"),
        new("pt-PT", "Portuguese (Portugal)", "🇵🇹"),
        // Other languages
        new("ar-SA", "Arabic (Saudi Arabia)", "🇸🇦"),
        new("ar-AE", "Arabic (United Arab Emirates)", "🇦🇪"),
        new("ru-RU", "Russian (Russia)", "🇷🇺"),
        new("hi-IN", "Hindi (India)", "🇮🇳"),
        new("id-ID", "Indonesian (Indonesia)", "🇮🇩"),
        new("vi-VN", "Vietnamese (Vietnam)", "🇻🇳"),
        new("th-TH", "Thai (Thailand)", "🇹🇭"),
        new("tr-TR", "Turkish (Türkiye)", "🇹🇷"),
        new("nl-NL", "Dutch (Netherlands)", "🇳🇱"),
        new("nl-BE", "Dutch (Belgium)", "🇧🇪"),
        new("pl-PL", "Polish (Poland)", "🇵🇱"),
        new("sv-SE", "Swedish (Sweden)", "🇸🇪"),
        new("da-DK", "Danish (Denmark)", "🇩🇰"),
        new("nb-NO", "Norwegian Bokmål (Norway)", "🇳🇴"),
        new("fi-FI", "Finnish (Finland)", "🇫🇮"),
        new("uk-UA", "Ukrainian (Ukraine)", "🇺🇦"),
        new("el-GR", "Greek (Greece)", "🇬🇷"),
        new("cs-CZ", "Czech (Czechia)", "🇨🇿"),
        new("sk-SK", "Slovak (Slovakia)", "🇸🇰"),
        new("hu-HU", "Hungarian (Hungary)", "🇭🇺"),
        new("ro-RO", "Romanian (Romania)", "🇷🇴"),
        new("hr-HR", "Croatian (Croatia)", "🇭🇷"),
        new("he-IL", "Hebrew (Israel)", "🇮🇱"),
        new("ms-MY", "Malay (Malaysia)", "🇲🇾"),
        new("ca-ES", "Catalan (Spain)", "🇪🇸"),
    ];
}

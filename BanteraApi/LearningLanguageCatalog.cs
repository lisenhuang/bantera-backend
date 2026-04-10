namespace BanteraApi;

/// <summary>
/// Curated learning / transcription locale catalog aligned with Gemini accent keys.
/// Excludes zh-TW (product uses zh / Mandarin Simplified only).
/// </summary>
public sealed record LearningLanguageItem(
    string Identifier,
    string DisplayName,
    string FlagEmoji);

public static class LearningLanguageCatalog
{
    /// <summary>Deterministic order matching <see cref="Gemini.GeminiService"/> accent keys (no zh-TW).</summary>
    public static IReadOnlyList<LearningLanguageItem> Items { get; } =
    [
        new("en-US", "English (United States)", "🇺🇸"),
        new("en-GB", "English (United Kingdom)", "🇬🇧"),
        new("en-UK", "English (United Kingdom)", "🇬🇧"),
        new("en-NZ", "English (New Zealand)", "🇳🇿"),
        new("en-AU", "English (Australia)", "🇦🇺"),
        new("en-CA", "English (Canada)", "🇨🇦"),
        new("en-IE", "English (Ireland)", "🇮🇪"),
        new("en-IN", "English (India)", "🇮🇳"),
        new("zh", "Chinese (Mandarin)", "🇨🇳"),
        new("ja", "Japanese", "🇯🇵"),
        new("ko", "Korean", "🇰🇷"),
        new("fr", "French", "🇫🇷"),
        new("de", "German", "🇩🇪"),
        new("es", "Spanish", "🇪🇸"),
        new("pt", "Portuguese", "🇵🇹"),
        new("hi", "Hindi", "🇮🇳"),
        new("ar", "Arabic", "🌐"),
        new("it", "Italian", "🇮🇹"),
        new("si", "Sinhala", "🇱🇰"),
    ];
}

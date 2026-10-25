namespace BanteraApi.Admin;

/// <summary>Private TTS experiments do not require the app's transcription support.</summary>
internal static class AdminAudioTestLanguages
{
    private static readonly LearningLanguageItem Maori = new("mi-NZ", "Māori (New Zealand)", "🇳🇿");

    public static LearningLanguageItem? Find(string? languageCode) =>
        languageCode == Maori.Identifier ? Maori :
            LearningLanguageCatalog.Items.FirstOrDefault(language => language.Identifier == languageCode);

    // Used for both new tests and reusing a saved test's dialogue.
    public static bool Supports(string? languageCode) =>
        languageCode == Maori.Identifier || LearningLanguageCatalog.SupportsAudioDialogues(languageCode);
}

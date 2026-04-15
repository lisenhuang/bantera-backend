namespace BanteraApi.Gemini;

public record GenerateAudioRequest(
    string Language,
    string LanguageCode,
    string Scenario,
    int DurationSeconds,
    string? ScenarioId = null,
    string? NativeLanguage = null,
    string? NativeLanguageCode = null);

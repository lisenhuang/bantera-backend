namespace BanteraApi.Gemini;

public record GenerateAudioRequest(
    string Language,
    string LanguageCode,
    string Scenario,
    int DurationSeconds);

namespace BanteraApi.Gemini;

public record SynthesiseAudioRequest(
    string Language,
    string LanguageCode,
    string Title,
    string Voice1,
    string Voice2,
    DialogueLine[] Lines);

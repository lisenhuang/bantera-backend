namespace BanteraApi.Gemini;

public record GeneratedDialogueResponse(
    string Title,
    string Voice1,
    string Voice2,
    DialogueLine[] Lines,
    string Language,
    string LanguageCode);

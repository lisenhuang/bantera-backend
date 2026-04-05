namespace BanteraApi.Gemini;

public class GeminiSettings
{
    public string[] ApiKeys { get; set; } = [];
    public string TextModel { get; set; } = "gemini-3-flash-preview";
    public string AudioModel { get; set; } = "gemini-2.5-flash-preview-tts";
}

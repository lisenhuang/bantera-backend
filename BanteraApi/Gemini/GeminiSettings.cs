namespace BanteraApi.Gemini;

public class GeminiSettings
{
    public string[] ApiKeys { get; set; } = [];
    public string TextModel { get; set; } = "gemini-flash-latest";
    public string LatestNewsTextModel { get; set; } = "gemini-2.5-flash";
    public string CueTimingModel { get; set; } = "gemini-flash-latest";
    public string AudioModel { get; set; } = "gemini-2.5-flash-preview-tts";
}

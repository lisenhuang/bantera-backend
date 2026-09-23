namespace BanteraApi.Gemini;

public class GeminiSettings
{
    public string[] ApiKeys { get; set; } = [];

    /// <summary>Default text model (dialogue, transcript fixes, alignment). Admins can override it in the dashboard.</summary>
    public string TextModel { get; set; } = "gemini-flash-lite-latest";

    /// <summary>Model for web-search generation. Only 2.5 supports the google_search tool on these keys.</summary>
    public string LatestNewsTextModel { get; set; } = "gemini-2.5-flash";

    /// <summary>Only keys with this prefix can use web search; other calls may use any key.</summary>
    public string WebSearchKeyPrefix { get; set; } = "AIzaSy";

    /// <summary>Fallback line-timing model, used only when word-level transcription fails.</summary>
    public string CueTimingModel { get; set; } = "gemini-flash-latest";

    /// <summary>Default TTS model. Admins can override it in the dashboard.</summary>
    public string AudioModel { get; set; } = "gemini-2.5-flash-preview-tts";

    /// <summary>Speech-to-text model that returns word-level timestamps (Interactions API).</summary>
    public string TranscribeModel { get; set; } = "gemini-3.5-transcribe";
}

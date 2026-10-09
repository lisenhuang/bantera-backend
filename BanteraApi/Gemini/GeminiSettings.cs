namespace BanteraApi.Gemini;

public class GeminiSettings
{
    public string[] ApiKeys { get; set; } = [];

    /// <summary>Default text model (dialogue, transcript fixes, alignment). Admins can override it in the dashboard.</summary>
    public string TextModel { get; set; } = "gemini-flash-lite-latest";

    /// <summary>Legacy config field. Server search now enforces AiSearchPolicy.GeminiModel unless an admin selects GPT.</summary>
    public string LatestNewsTextModel { get; set; } = "gemini-2.5-flash";

    /// <summary>Legacy config field. Search always enforces AiSearchPolicy.GeminiKeyPrefix; other calls may use any key.</summary>
    public string WebSearchKeyPrefix { get; set; } = "AIzaSy";

    /// <summary>Fallback line-timing model, used only when word-level transcription fails.</summary>
    public string CueTimingModel { get; set; } = "gemini-flash-latest";

    /// <summary>Default TTS model. Admins can override it in the dashboard.</summary>
    public string AudioModel { get; set; } = "gemini-3.1-flash-tts-preview";

    /// <summary>Speech-to-text model that returns word-level timestamps (Interactions API).</summary>
    public string TranscribeModel { get; set; } = "gemini-3.5-transcribe";
}

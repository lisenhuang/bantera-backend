namespace BanteraApi.RevAi;

public class RevAiSettings
{
    public const string Section = "RevAi";

    public string AccessToken { get; set; } = string.Empty;
    public bool LogTranscriptPreview { get; set; } = true;
    public bool LogTranscriptFull { get; set; } = false;
    public int TranscriptPreviewMaxChars { get; set; } = 1000;
    public bool LogDelimitedJsonDebug { get; set; } = true;
    public int LogResponseBodyMaxChars { get; set; } = 20000;
}

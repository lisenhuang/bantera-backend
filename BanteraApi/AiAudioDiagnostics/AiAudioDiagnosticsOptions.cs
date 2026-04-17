namespace BanteraApi.Diagnostics;

public class AiAudioDiagnosticsOptions
{
    public const string Section = "AiAudioDiagnostics";

    public string Directory { get; set; } = "diagnostics";

    public bool IncludeFullText { get; set; } = true;

    public int MaxPreviewChars { get; set; } = 500;
}

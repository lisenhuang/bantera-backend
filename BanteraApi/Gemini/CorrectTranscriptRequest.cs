namespace BanteraApi.Gemini;

public record CorrectTranscriptRequest(
    string[] OriginalLines,
    Videos.VideoTranscriptCue[] TranscribedCues);

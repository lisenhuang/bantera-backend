using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BanteraApi.Videos;

public sealed class UploadVideoRequest
{
    [FromForm(Name = "file")]
    public IFormFile File { get; init; } = default!;

    [FromForm(Name = "transcriptText")]
    public string TranscriptText { get; init; } = string.Empty;

    [FromForm(Name = "transcriptLanguage")]
    public string TranscriptLanguage { get; init; } = string.Empty;

    [FromForm(Name = "transcriptLanguageCode")]
    public string TranscriptLanguageCode { get; init; } = string.Empty;

    [FromForm(Name = "transcriptCuesJson")]
    public string TranscriptCuesJson { get; init; } = string.Empty;

    [FromForm(Name = "isPublic")]
    public bool IsPublic { get; init; }

    [FromForm(Name = "durationMs")]
    public int DurationMs { get; init; }

    [FromForm(Name = "videoWidth")]
    public int? VideoWidth { get; init; }

    [FromForm(Name = "videoHeight")]
    public int? VideoHeight { get; init; }
}

using System.IO;
using System.Text.Json;
using BanteraApi.Auth;
using BanteraApi.Cloudflare;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Videos;

public class VideoService(
    AppDbContext db,
    R2StorageService r2StorageService,
    LinkGenerator linkGenerator,
    CloudflareImageService cloudflareImageService)
{
    private static readonly JsonSerializerOptions TranscriptJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> SupportedVideoContentTypes =
    [
        "video/mp4",
        "video/quicktime",
        "video/x-m4v",
    ];

    private const long MaxVideoBytes = 250L * 1024 * 1024;
    private const int MaxTranscriptLength = 100_000;
    private const int MaxTranscriptLanguageCodeLength = 16;
    private const int MaxDurationMs = 6 * 60 * 60 * 1000;
    private const int MaxCueCount = 5_000;

    public async Task<(VideoUploadResponse? Response, string? ErrorCode)> UploadVideoAsync(
        Guid userId,
        UploadVideoRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (request.File is null || request.File.Length <= 0 || request.File.Length > MaxVideoBytes)
            return (null, ErrorCodes.InvalidVideoUpload);

        var transcriptLanguage = NormalizeTranscriptLanguage(request.TranscriptLanguage);
        var transcriptLanguageCode = NormalizeTranscriptLanguageCode(
            request.TranscriptLanguageCode,
            transcriptLanguage);
        var transcriptCues = ParseTranscriptCues(request.TranscriptCuesJson);
        var transcriptText = BuildTranscriptText(transcriptCues, request.TranscriptText);
        var contentType = NormalizeContentType(request.File.ContentType, request.File.FileName);
        var originalFileName = NormalizeFileName(request.File.FileName);

        if (string.IsNullOrWhiteSpace(transcriptText)
            || transcriptText.Length > MaxTranscriptLength
            || string.IsNullOrWhiteSpace(transcriptLanguage)
            || string.IsNullOrWhiteSpace(transcriptLanguageCode)
            || transcriptLanguageCode.Length > MaxTranscriptLanguageCodeLength
            || transcriptCues.Count == 0
            || contentType is null
            || request.DurationMs <= 0
            || request.DurationMs > MaxDurationMs
            || !IsValidDimension(request.VideoWidth)
            || !IsValidDimension(request.VideoHeight))
        {
            return (null, ErrorCodes.InvalidVideoUpload);
        }

        var userExists = await db.Users.AnyAsync(u => u.Id == userId, cancellationToken);
        if (!userExists)
            return (null, ErrorCodes.Unauthorized);

        var video = new UserVideo
        {
            UserId = userId,
            MediaObjectKey = BuildVideoObjectKey(userId, contentType),
            MediaContentType = contentType,
            OriginalFileName = originalFileName,
            TranscriptText = transcriptText,
            TranscriptLanguage = transcriptLanguage,
            TranscriptLanguageCode = transcriptLanguageCode,
            TranscriptCuesJson = JsonSerializer.Serialize(transcriptCues, TranscriptJsonOptions),
            IsPublic = request.IsPublic,
            FileSizeBytes = request.File.Length,
            DurationMs = request.DurationMs,
            VideoWidth = request.VideoWidth,
            VideoHeight = request.VideoHeight,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using var input = request.File.OpenReadStream();
        await r2StorageService.UploadObjectAsync(
            video.MediaObjectKey,
            input,
            contentType,
            cancellationToken);

        db.UserVideos.Add(video);
        await db.SaveChangesAsync(cancellationToken);

        return (BuildResponse(video, httpContext), null);
    }

    public async Task<VideoUploadResponse?> GetVideoAsync(
        Guid videoId,
        Guid? requesterUserId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var video = await db.UserVideos
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken);

        if (video is null || !CanAccess(video, requesterUserId))
            return null;

        return BuildResponse(video, httpContext);
    }

    public async Task<IReadOnlyList<VideoUploadResponse>> ListMyVideosAsync(
        Guid userId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var videos = await db.UserVideos
            .AsNoTracking()
            .Where(v => v.UserId == userId)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(cancellationToken);

        return videos
            .Select(video => BuildResponse(video, httpContext))
            .ToList();
    }

    public async Task<StoredObjectResult?> GetVideoFileAsync(
        Guid videoId,
        Guid? requesterUserId,
        CancellationToken cancellationToken = default)
    {
        var video = await db.UserVideos
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken);

        if (video is null || !CanAccess(video, requesterUserId))
            return null;

        return await r2StorageService.DownloadObjectAsync(video.MediaObjectKey, cancellationToken);
    }

    private VideoUploadResponse BuildResponse(UserVideo video, HttpContext httpContext)
    {
        return new VideoUploadResponse(
            video.Id,
            video.UserId,
            video.OriginalFileName,
            video.TranscriptText,
            video.TranscriptLanguage,
            video.TranscriptLanguageCode,
            ParseStoredTranscriptCues(video.TranscriptCuesJson),
            video.IsPublic,
            video.DurationMs,
            video.FileSizeBytes,
            video.VideoWidth,
            video.VideoHeight,
            video.MediaContentType,
            linkGenerator.GetUriByName(
                httpContext,
                "GetVideoFile",
                values: new { videoId = video.Id }),
            video.CoverImageObjectKey != null
                ? linkGenerator.GetUriByName(httpContext, "GetVideoCoverImage", values: new { videoId = video.Id })
                : null,
            video.IsAiGenerated,
            video.IsTranscriptionEstimated,
            video.CreatedAt);
    }

    private static bool CanAccess(UserVideo video, Guid? requesterUserId)
    {
        return video.IsPublic || (requesterUserId is not null && requesterUserId.Value == video.UserId);
    }

    private static string? NormalizeContentType(string? contentType, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var normalized = contentType.Trim().ToLowerInvariant();
            if (SupportedVideoContentTypes.Contains(normalized))
                return normalized;
        }

        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".mov" => "video/quicktime",
            ".m4v" => "video/x-m4v",
            ".mp4" => "video/mp4",
            _ => null,
        };
    }

    private static string NormalizeTranscript(string? transcriptText)
    {
        var normalized = (transcriptText ?? string.Empty)
            .Replace("\r\n", "\n")
            .Trim();
        return normalized;
    }

    private static string BuildTranscriptText(
        IReadOnlyList<VideoTranscriptCue> transcriptCues,
        string? transcriptText)
    {
        if (transcriptCues.Count > 0)
        {
            var joined = string.Join(
                "\n",
                transcriptCues
                    .Select(cue => cue.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            return NormalizeTranscript(joined);
        }

        return NormalizeTranscript(transcriptText);
    }

    private static string NormalizeTranscriptLanguage(string? transcriptLanguage)
    {
        var normalized = (transcriptLanguage ?? string.Empty)
            .Trim()
            .Replace('_', '-');

        return normalized.Length > 35
            ? normalized[..35]
            : normalized;
    }

    private static string NormalizeTranscriptLanguageCode(
        string? transcriptLanguageCode,
        string transcriptLanguage)
    {
        var normalized = (transcriptLanguageCode ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = transcriptLanguage.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Trim()
                .ToLowerInvariant()
                ?? string.Empty;

        return normalized.Length > MaxTranscriptLanguageCodeLength
            ? normalized[..MaxTranscriptLanguageCodeLength]
            : normalized;
    }

    private static IReadOnlyList<VideoTranscriptCue> ParseTranscriptCues(string? transcriptCuesJson)
    {
        if (string.IsNullOrWhiteSpace(transcriptCuesJson))
            return [];

        try
        {
            var cues = JsonSerializer.Deserialize<List<VideoTranscriptCue>>(
                    transcriptCuesJson,
                    TranscriptJsonOptions)
                ?? [];

            if (cues.Count == 0 || cues.Count > MaxCueCount)
                return [];

            var normalized = new List<VideoTranscriptCue>(cues.Count);
            var previousStartMs = -1;
            for (var index = 0; index < cues.Count; index++)
            {
                var cue = cues[index];
                var text = NormalizeTranscript(cue.Text);
                if (string.IsNullOrWhiteSpace(text))
                    return [];

                if (cue.StartMs < 0 || cue.EndMs <= cue.StartMs)
                    return [];

                if (previousStartMs > cue.StartMs)
                    return [];

                normalized.Add(new VideoTranscriptCue(
                    index,
                    cue.StartMs,
                    cue.EndMs,
                    text));
                previousStartMs = cue.StartMs;
            }

            return normalized;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<VideoTranscriptCue> ParseStoredTranscriptCues(string transcriptCuesJson)
    {
        return ParseTranscriptCues(transcriptCuesJson);
    }

    private static string NormalizeFileName(string? fileName)
    {
        var normalized = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "video-upload";

        return normalized.Length > 255
            ? normalized[..255]
            : normalized;
    }

    private static bool IsValidDimension(int? value)
    {
        return value is null || (value.Value > 0 && value.Value <= 10_000);
    }

    public async Task<VideoUploadResponse> SaveAiAudioAsync(
        Guid userId,
        string title,
        byte[] wavBytes,
        string transcriptLanguage,
        string transcriptLanguageCode,
        IReadOnlyList<Gemini.VideoTranscriptCueRecord> cues,
        int durationMs,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var objectKey = $"videos/{userId}/{Guid.NewGuid():N}.wav";
        await r2StorageService.UploadObjectAsync(objectKey, new MemoryStream(wavBytes), "audio/wav", cancellationToken);

        // Generate cover image via Cloudflare Workers AI (best-effort — failure does not block audio creation).
        string? coverKey = null;
        try
        {
            var imagePrompt = $"A vibrant, artistic illustration representing a conversation in {transcriptLanguage} about '{title}'. No text, no letters, clean modern art style.";
            var coverBytes = await cloudflareImageService.GenerateImageAsync(imagePrompt, cancellationToken);
            coverKey = $"covers/{userId}/{Guid.NewGuid():N}.png";
            await r2StorageService.UploadObjectAsync(coverKey, new MemoryStream(coverBytes), "image/png", cancellationToken);
        }
        catch
        {
            coverKey = null;
        }

        var transcriptText = string.Join("\n", cues.Select(c => c.Text));
        var cuesJson = JsonSerializer.Serialize(
            cues.Select(c => new VideoTranscriptCue(c.Index, c.StartMs, c.EndMs, c.Text)).ToList(),
            TranscriptJsonOptions);

        var video = new Database.Entities.UserVideo
        {
            UserId = userId,
            MediaObjectKey = objectKey,
            MediaContentType = "audio/wav",
            OriginalFileName = $"{title}.wav",
            TranscriptText = transcriptText,
            TranscriptLanguage = NormalizeTranscriptLanguage(transcriptLanguage),
            TranscriptLanguageCode = NormalizeTranscriptLanguageCode(transcriptLanguageCode, transcriptLanguage),
            TranscriptCuesJson = cuesJson,
            IsPublic = true,
            IsAiGenerated = true,
            IsTranscriptionEstimated = true,
            CoverImageObjectKey = coverKey,
            FileSizeBytes = wavBytes.Length,
            DurationMs = durationMs,
            VideoWidth = null,
            VideoHeight = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.UserVideos.Add(video);
        await db.SaveChangesAsync(cancellationToken);
        return BuildResponse(video, httpContext);
    }

    public async Task<bool> DeleteVideoAsync(
        Guid videoId,
        Guid requesterId,
        CancellationToken cancellationToken = default)
    {
        var video = await db.UserVideos
            .FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken);

        if (video is null || video.UserId != requesterId)
            return false;

        await r2StorageService.DeleteObjectAsync(video.MediaObjectKey, cancellationToken);
        db.UserVideos.Remove(video);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<VideoUploadResponse?> UpdateTranscriptAsync(
        Guid videoId,
        Guid requesterId,
        string transcriptText,
        IReadOnlyList<VideoTranscriptCue> cues,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var video = await db.UserVideos
            .FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken);

        if (video is null || video.UserId != requesterId)
            return null;

        video.TranscriptText = transcriptText;
        video.TranscriptCuesJson = JsonSerializer.Serialize(cues, TranscriptJsonOptions);
        video.IsTranscriptionEstimated = false;
        video.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return BuildResponse(video, httpContext);
    }

    private static string BuildVideoObjectKey(Guid userId, string contentType)
    {
        var extension = contentType switch
        {
            "video/quicktime" => "mov",
            "video/x-m4v" => "m4v",
            _ => "mp4",
        };

        return $"videos/{userId}/{Guid.NewGuid():N}.{extension}";
    }
}

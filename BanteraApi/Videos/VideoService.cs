using System.IO;
using BanteraApi.Auth;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Videos;

public class VideoService(
    AppDbContext db,
    R2StorageService r2StorageService,
    LinkGenerator linkGenerator)
{
    private static readonly HashSet<string> SupportedVideoContentTypes =
    [
        "video/mp4",
        "video/quicktime",
        "video/x-m4v",
    ];

    private const long MaxVideoBytes = 250L * 1024 * 1024;
    private const int MaxTranscriptLength = 100_000;
    private const int MaxDurationMs = 6 * 60 * 60 * 1000;

    public async Task<(VideoUploadResponse? Response, string? ErrorCode)> UploadVideoAsync(
        Guid userId,
        UploadVideoRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (request.File is null || request.File.Length <= 0 || request.File.Length > MaxVideoBytes)
            return (null, ErrorCodes.InvalidVideoUpload);

        var transcriptText = NormalizeTranscript(request.TranscriptText);
        var transcriptLanguage = NormalizeTranscriptLanguage(request.TranscriptLanguage);
        var contentType = NormalizeContentType(request.File.ContentType, request.File.FileName);
        var originalFileName = NormalizeFileName(request.File.FileName);

        if (string.IsNullOrWhiteSpace(transcriptText)
            || transcriptText.Length > MaxTranscriptLength
            || string.IsNullOrWhiteSpace(transcriptLanguage)
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

    private static string NormalizeTranscript(string transcriptText)
    {
        var normalized = transcriptText.Replace("\r\n", "\n").Trim();
        return normalized;
    }

    private static string NormalizeTranscriptLanguage(string transcriptLanguage)
    {
        var normalized = transcriptLanguage
            .Trim()
            .Replace('_', '-');

        return normalized.Length > 35
            ? normalized[..35]
            : normalized;
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

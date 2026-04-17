using System.IO;
using System.Data;
using System.Text.Json;
using BanteraApi.Auth;
using BanteraApi.Cloudflare;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Gemini;
using BanteraApi.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace BanteraApi.Videos;

public class VideoService(
    AppDbContext db,
    R2StorageService r2StorageService,
    LinkGenerator linkGenerator,
    CloudflareImageService cloudflareImageService,
    ILogger<VideoService> logger)
{
    private static readonly JsonSerializerOptions TranscriptJsonOptions = new(JsonSerializerDefaults.Web);
    private const string HighlightStart = "\u001b[30;103m";
    private const string HighlightEnd = "\u001b[0m";
    private const int FallbackCoverSize = 512;

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
            .FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken);

        if (video is null || !CanAccess(video, requesterUserId))
            return null;

        await EnsureAiAudioCoverAsync(video, cancellationToken);
        return BuildResponse(video, httpContext);
    }

    public async Task<IReadOnlyList<VideoUploadResponse>> ListMyVideosAsync(
        Guid userId,
        HttpContext httpContext,
        bool includeV2 = false,
        CancellationToken cancellationToken = default)
    {
        var query = ApplyV2Filter(db.UserVideos.AsNoTracking(), includeV2)
            .AsNoTracking()
            .Where(v => v.UserId == userId && v.RemovedFromOwnerListAt == null);

        var videos = await query.OrderByDescending(v => v.CreatedAt).ToListAsync(cancellationToken);

        return videos
            .Select(video => BuildResponse(video, httpContext))
            .ToList();
    }

    /// <summary>
    /// Returns public videos whose transcript language matches
    /// <paramref name="languageCode"/>, with optional full-text
    /// <paramref name="searchQuery"/> (matched against file name and transcript
    /// text), keyset-paged via <paramref name="offset"/> / <paramref name="limit"/>.
    /// Videos owned by <paramref name="excludeUserId"/> are excluded when provided.
    /// </summary>
    public async Task<IReadOnlyList<VideoUploadResponse>> ListPublicVideosAsync(
        string? languageCode,
        Guid? excludeUserId,
        int limit,
        int offset,
        string? searchQuery,
        string? mediaType,
        HttpContext httpContext,
        bool includeV2 = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = languageCode?.Trim().ToLowerInvariant();

        // Extract the primary language subtag (e.g. "en" from "en-US").
        var primaryCode = string.IsNullOrWhiteSpace(normalizedCode)
            ? null
            : normalizedCode.Contains('-')
                ? normalizedCode[..normalizedCode.IndexOf('-')]
                : null;

        var query = ApplyV2Filter(db.UserVideos.AsNoTracking(), includeV2)
            .Where(v => v.IsPublic);

        var mt = mediaType?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(mt))
            query = query.Where(v => v.MediaContentType.ToLower().StartsWith(mt + "/"));

        if (!string.IsNullOrWhiteSpace(normalizedCode))
        {
            if (primaryCode is not null)
            {
                query = query.Where(v =>
                    v.TranscriptLanguageCode.ToLower() == normalizedCode ||
                    v.TranscriptLanguageCode.ToLower() == primaryCode);
            }
            else
            {
                query = query.Where(v =>
                    v.TranscriptLanguageCode.ToLower() == normalizedCode ||
                    v.TranscriptLanguageCode.ToLower().StartsWith(normalizedCode + "-"));
            }
        }

        if (excludeUserId.HasValue)
            query = query.Where(v => v.UserId != excludeUserId.Value);

        // Full-text search across file name and transcript text.
        var search = searchQuery?.Trim().ToLower();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(v =>
                v.OriginalFileName.ToLower().Contains(search) ||
                v.TranscriptText.ToLower().Contains(search));
        }

        var result = await query
            .Join(db.Users, v => v.UserId, u => u.Id, (v, u) => new { Video = v, CreatorName = u.Name })
            .OrderByDescending(x => x.Video.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return result
            .Select(x => BuildResponse(x.Video, httpContext, x.CreatorName))
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

    private VideoUploadResponse BuildResponse(UserVideo video, HttpContext httpContext, string? creatorDisplayName = null)
    {
        return new VideoUploadResponse(
            video.Id,
            video.UserId,
            video.OriginalFileName,
            video.TranscriptText,
            video.TranscriptLanguage,
            video.TranscriptLanguageCode,
            ParseTranscriptCues(video.TranscriptCuesJson),
            ParseOptionalStoredTranscriptCues(video.TranscriptShortCuesJson),
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
            video.CreatedAt,
            creatorDisplayName,
            video.TranscriptionVersion,
            ParseDialogueLines(video.DialogueLinesJson),
            ParseWordTiming(video.WordTimingJson));
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

    private static IReadOnlyList<VideoTranscriptCue>? ParseOptionalStoredTranscriptCues(string? transcriptCuesJson)
    {
        var parsed = ParseTranscriptCues(transcriptCuesJson);
        return parsed.Count > 0 ? parsed : null;
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

        var coverKey = await TryGenerateAiAudioCoverAsync(
            userId,
            title,
            transcriptLanguage,
            cancellationToken);

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

    public async Task<VideoUploadResponse> SaveAiAudioV2Async(
        Guid userId,
        string title,
        string objectKey,
        long fileSizeBytes,
        string transcriptLanguage,
        string transcriptLanguageCode,
        DialogueLine[] dialogueLines,
        IReadOnlyList<WordTimingRecord>? wordTiming,
        IReadOnlyList<Gemini.VideoTranscriptCueRecord> cues,
        IReadOnlyList<Gemini.VideoTranscriptCueRecord>? shortCues,
        int durationMs,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var coverKey = await TryGenerateAiAudioCoverAsync(
            userId,
            title,
            transcriptLanguage,
            cancellationToken);

        var transcriptText = string.Join("\n", cues.Select(c => c.Text));
        var cuesJson = JsonSerializer.Serialize(
            cues.Select(c => new VideoTranscriptCue(c.Index, c.StartMs, c.EndMs, c.Text)).ToList(),
            TranscriptJsonOptions);
        var shortCuesJson = shortCues is { Count: > 0 }
            ? JsonSerializer.Serialize(
                shortCues.Select(c => new VideoTranscriptCue(c.Index, c.StartMs, c.EndMs, c.Text)).ToList(),
                TranscriptJsonOptions)
            : null;

        var video = new UserVideo
        {
            UserId = userId,
            MediaObjectKey = objectKey,
            MediaContentType = "audio/wav",
            OriginalFileName = $"{title}.wav",
            TranscriptText = transcriptText,
            TranscriptLanguage = NormalizeTranscriptLanguage(transcriptLanguage),
            TranscriptLanguageCode = NormalizeTranscriptLanguageCode(transcriptLanguageCode, transcriptLanguage),
            TranscriptCuesJson = cuesJson,
            TranscriptShortCuesJson = shortCuesJson,
            TranscriptionVersion = 1,
            DialogueLinesJson = JsonSerializer.Serialize(dialogueLines.Select(l => l.Text).ToArray(), TranscriptJsonOptions),
            WordTimingJson = wordTiming is null
                ? null
                : JsonSerializer.Serialize(wordTiming, TranscriptJsonOptions),
            IsPublic = true,
            IsAiGenerated = true,
            IsTranscriptionEstimated = false,
            CoverImageObjectKey = coverKey,
            FileSizeBytes = fileSizeBytes,
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

    private async Task EnsureAiAudioCoverAsync(
        UserVideo video,
        CancellationToken cancellationToken)
    {
        if (!ShouldGenerateAiAudioCover(video))
            return;

        var title = GetAiAudioTitle(video);
        var generatedCoverKey = await TryGenerateAiAudioCoverAsync(
            video.UserId,
            title,
            video.TranscriptLanguage,
            cancellationToken);

        if (generatedCoverKey is null)
            return;

        video.CoverImageObjectKey = generatedCoverKey;
        video.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string?> TryGenerateAiAudioCoverAsync(
        Guid userId,
        string title,
        string transcriptLanguage,
        CancellationToken cancellationToken)
    {
        try
        {
            var imagePrompt = BuildAiAudioCoverPrompt(transcriptLanguage, title);
            var pngBytes = await cloudflareImageService.GenerateImageAsync(imagePrompt, cancellationToken);
            using var img = Image.Load(pngBytes);
            using var jpegMs = new MemoryStream();
            await img.SaveAsJpegAsync(jpegMs, new JpegEncoder { Quality = 85 }, cancellationToken);

            var coverBytes = jpegMs.ToArray();
            var coverKey = $"covers/{userId}/{Guid.NewGuid():N}.jpg";
            await r2StorageService.UploadObjectAsync(
                coverKey,
                new MemoryStream(coverBytes),
                "image/jpeg",
                cancellationToken);

            return coverKey;
        }
        catch (Exception ex)
        {
            WriteHighlightedTerminalMessage(
                $"[AI Cover] Failed to generate cover image. UserId={userId} Language={transcriptLanguage} Title={title}");
            logger.LogError(
                ex,
                "{HighlightStart}[AI Cover] Failed to generate cover image. UserId={UserId} Language={TranscriptLanguage} Title={Title}{HighlightEnd}",
                HighlightStart,
                userId,
                transcriptLanguage,
                title,
                HighlightEnd);

            return await TryGenerateFallbackCoverAsync(
                userId,
                title,
                transcriptLanguage,
                cancellationToken);
        }
    }

    private async Task<string?> TryGenerateFallbackCoverAsync(
        Guid userId,
        string title,
        string transcriptLanguage,
        CancellationToken cancellationToken)
    {
        try
        {
            var coverBytes = GenerateFallbackCoverBytes(title, transcriptLanguage);
            var coverKey = $"covers/{userId}/{Guid.NewGuid():N}.jpg";
            await r2StorageService.UploadObjectAsync(
                coverKey,
                new MemoryStream(coverBytes),
                "image/jpeg",
                cancellationToken);

            logger.LogWarning(
                "[AI Cover] Using locally generated fallback cover. UserId={UserId} Language={TranscriptLanguage} Title={Title}",
                userId,
                transcriptLanguage,
                title);

            return coverKey;
        }
        catch (Exception ex)
        {
            WriteHighlightedTerminalMessage(
                $"[AI Cover] Fallback cover generation also failed. UserId={userId} Language={transcriptLanguage} Title={title}");
            logger.LogError(
                ex,
                "{HighlightStart}[AI Cover] Fallback cover generation also failed. UserId={UserId} Language={TranscriptLanguage} Title={Title}{HighlightEnd}",
                HighlightStart,
                userId,
                transcriptLanguage,
                title,
                HighlightEnd);
            return null;
        }
    }

    private static bool ShouldGenerateAiAudioCover(UserVideo video)
    {
        return video.IsAiGenerated
            && video.CoverImageObjectKey is null
            && video.MediaContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAiAudioTitle(UserVideo video)
    {
        var title = Path.GetFileNameWithoutExtension(video.OriginalFileName).Trim();
        return string.IsNullOrWhiteSpace(title)
            ? "Bantera lesson"
            : title;
    }

    private static string BuildAiAudioCoverPrompt(string transcriptLanguage, string title)
    {
        return $"A vibrant, artistic illustration representing a conversation in {transcriptLanguage} about '{title}'. No text, no letters, clean modern art style.";
    }

    private static byte[] GenerateFallbackCoverBytes(string title, string transcriptLanguage)
    {
        var seed = HashCode.Combine(title, transcriptLanguage);
        var topColor = BuildPaletteColor(seed, 0.78f);
        var bottomColor = BuildPaletteColor(seed * 31, 0.52f);
        var accentColor = BuildPaletteColor(seed * 131, 0.92f);

        using var image = new Image<Rgba32>(FallbackCoverSize, FallbackCoverSize);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var verticalMix = (float)y / Math.Max(1, accessor.Height - 1);

                for (var x = 0; x < row.Length; x++)
                {
                    var horizontalMix = (float)x / Math.Max(1, row.Length - 1);
                    var blended = Lerp(topColor, bottomColor, verticalMix);

                    var distanceFromCenter = MathF.Abs(horizontalMix - 0.5f);
                    if (distanceFromCenter < 0.18f)
                    {
                        var accentMix = (0.18f - distanceFromCenter) / 0.18f;
                        blended = Lerp(blended, accentColor, accentMix * 0.75f);
                    }

                    var diagonalBand = MathF.Abs((horizontalMix * 0.8f) + (verticalMix * 0.65f) - 0.72f);
                    if (diagonalBand < 0.07f)
                    {
                        var bandMix = (0.07f - diagonalBand) / 0.07f;
                        blended = Lerp(blended, accentColor, bandMix * 0.55f);
                    }

                    row[x] = blended;
                }
            }
        });

        using var jpegMs = new MemoryStream();
        image.SaveAsJpeg(jpegMs, new JpegEncoder { Quality = 85 });
        return jpegMs.ToArray();
    }

    private static Rgba32 BuildPaletteColor(int seed, float lightnessBias)
    {
        var normalizedSeed = Math.Abs(seed % 360);
        var hue = normalizedSeed / 360f;
        var saturation = 0.48f + ((Math.Abs(seed % 17) / 16f) * 0.24f);
        var lightness = Math.Clamp(lightnessBias, 0.18f, 0.92f);
        return HslToRgba(hue, saturation, lightness);
    }

    private static Rgba32 Lerp(Rgba32 a, Rgba32 b, float t)
    {
        var clamped = Math.Clamp(t, 0f, 1f);
        return new Rgba32(
            (byte)(a.R + ((b.R - a.R) * clamped)),
            (byte)(a.G + ((b.G - a.G) * clamped)),
            (byte)(a.B + ((b.B - a.B) * clamped)));
    }

    private static Rgba32 HslToRgba(float h, float s, float l)
    {
        if (s <= 0f)
        {
            var gray = (byte)Math.Clamp((int)Math.Round(l * 255f), 0, 255);
            return new Rgba32(gray, gray, gray);
        }

        static float HueToChannel(float p, float q, float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + ((q - p) * 6f * t);
            if (t < 1f / 2f) return q;
            if (t < 2f / 3f) return p + ((q - p) * ((2f / 3f) - t) * 6f);
            return p;
        }

        var q = l < 0.5f
            ? l * (1f + s)
            : l + s - (l * s);
        var p = (2f * l) - q;

        var r = HueToChannel(p, q, h + (1f / 3f));
        var g = HueToChannel(p, q, h);
        var b = HueToChannel(p, q, h - (1f / 3f));

        return new Rgba32(
            (byte)Math.Clamp((int)Math.Round(r * 255f), 0, 255),
            (byte)Math.Clamp((int)Math.Round(g * 255f), 0, 255),
            (byte)Math.Clamp((int)Math.Round(b * 255f), 0, 255));
    }

    private static void WriteHighlightedTerminalMessage(string message)
    {
        Console.Error.WriteLine($"{HighlightStart}{message}{HighlightEnd}");
    }

    public async Task<bool> SaveVideoAsync(
        Guid userId,
        Guid videoId,
        CancellationToken cancellationToken = default)
    {
        var alreadySaved = await db.UserSavedVideos
            .AnyAsync(s => s.UserId == userId && s.VideoId == videoId, cancellationToken);
        if (alreadySaved) return true;

        var videoExists = await db.UserVideos
            .AnyAsync(v => v.Id == videoId && v.IsPublic, cancellationToken);
        if (!videoExists) return false;

        db.UserSavedVideos.Add(new Database.Entities.UserSavedVideo
        {
            UserId = userId,
            VideoId = videoId,
            SavedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UnsaveVideoAsync(
        Guid userId,
        Guid videoId,
        CancellationToken cancellationToken = default)
    {
        var saved = await db.UserSavedVideos
            .FirstOrDefaultAsync(s => s.UserId == userId && s.VideoId == videoId, cancellationToken);
        if (saved is null) return;
        db.UserSavedVideos.Remove(saved);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> IsVideoSavedAsync(
        Guid userId,
        Guid videoId,
        CancellationToken cancellationToken = default)
    {
        return await db.UserSavedVideos
            .AnyAsync(s => s.UserId == userId && s.VideoId == videoId, cancellationToken);
    }

    public async Task<IReadOnlyList<SavedCueResponse>> ListSavedCuesAsync(
        Guid userId,
        HttpContext httpContext,
        bool includeV2 = false,
        CancellationToken cancellationToken = default)
    {
        var entries = await db.UserSavedCues
            .Where(c => c.UserId == userId)
            .Join(db.UserVideos, c => c.VideoId, v => v.Id, (c, v) => new { Cue = c, Video = v })
            .Where(x => includeV2 || x.Video.TranscriptionVersion == null || x.Video.TranscriptionVersion == 0)
            .Join(db.Users, x => x.Video.UserId, u => u.Id, (x, u) => new { x.Cue, x.Video, CreatorName = u.Name })
            .OrderByDescending(x => x.Cue.SavedAt)
            .ToListAsync(cancellationToken);
        var metadataById = await LoadSavedCueMetadataAsync(
            entries.Select(x => x.Cue.Id).ToArray(),
            cancellationToken);

        return entries
            .Select(x =>
            {
                metadataById.TryGetValue(x.Cue.Id, out var metadata);
                return new SavedCueResponse(
                    x.Cue.Id,
                    x.Cue.CueId,
                    x.Cue.CueIndex,
                    metadata?.CueText,
                    metadata?.StartTimeMs,
                    metadata?.EndTimeMs,
                    metadata?.CueMode,
                    metadata?.ParentCueId,
                    metadata?.ParentCueIndex,
                    x.Cue.SavedAt,
                    BuildResponse(x.Video, httpContext, x.CreatorName));
            })
            .ToList();
    }

    private async Task<Dictionary<Guid, SavedCueSegmentMetadata>> LoadSavedCueMetadataAsync(
        Guid[] savedCueIds,
        CancellationToken cancellationToken)
    {
        if (savedCueIds.Length == 0)
            return [];
        if (!await SavedCueMetadataColumnsExistAsync(cancellationToken))
            return [];

        var result = new Dictionary<Guid, SavedCueSegmentMetadata>();
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    "Id",
                    "CueText",
                    "StartTimeMs",
                    "EndTimeMs",
                    "CueMode",
                    "ParentCueId",
                    "ParentCueIndex"
                FROM user_saved_cues
                WHERE "Id" = ANY(@ids)
                """;
            AddDbParameter(command, "ids", savedCueIds);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                result[id] = new SavedCueSegmentMetadata(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not load saved cue segment metadata; returning legacy saved cue fields only.");
            return [];
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }

        return result;
    }

    private async Task<bool> SavedCueMetadataColumnsExistAsync(CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM information_schema.columns
                WHERE table_name = 'user_saved_cues'
                  AND column_name IN (
                      'CueText',
                      'StartTimeMs',
                      'EndTimeMs',
                      'CueMode',
                      'ParentCueId',
                      'ParentCueIndex'
                  )
                """;
            var count = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(count) == 6;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static void AddDbParameter(IDbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record SavedCueSegmentMetadata(
        string? CueText,
        int? StartTimeMs,
        int? EndTimeMs,
        string? CueMode,
        string? ParentCueId,
        int? ParentCueIndex);

    public async Task<IReadOnlyList<VideoUploadResponse>> ListSavedVideosAsync(
        Guid userId,
        HttpContext httpContext,
        bool includeV2 = false,
        CancellationToken cancellationToken = default)
    {
        var result = await db.UserSavedVideos
            .Where(s => s.UserId == userId)
            .Join(db.UserVideos, s => s.VideoId, v => v.Id, (s, v) => new { Saved = s, Video = v })
            .Where(x => includeV2 || x.Video.TranscriptionVersion == null || x.Video.TranscriptionVersion == 0)
            .Join(db.Users, x => x.Video.UserId, u => u.Id, (x, u) => new { x.Saved, x.Video, CreatorName = u.Name })
            .OrderByDescending(x => x.Saved.SavedAt)
            .ToListAsync(cancellationToken);

        return result
            .Select(x => BuildResponse(x.Video, httpContext, x.CreatorName))
            .ToList();
    }

    public async Task<int> GetUploadCountAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await db.UserVideos.CountAsync(
            v => v.UserId == userId && v.RemovedFromOwnerListAt == null,
            cancellationToken);
    }

    public async Task<bool> RemoveAiAudioFromOwnerListAsync(
        Guid videoId,
        Guid requesterId,
        CancellationToken cancellationToken = default)
    {
        var video = await db.UserVideos
            .FirstOrDefaultAsync(v => v.Id == videoId && v.UserId == requesterId, cancellationToken);

        if (video is null || !video.IsAiGenerated || !IsAudioContentType(video.MediaContentType))
            return false;

        if (video.RemovedFromOwnerListAt is not null)
            return true;

        var now = DateTime.UtcNow;
        video.RemovedFromOwnerListAt = now;
        video.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> GetSavedCountAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await db.UserSavedVideos.CountAsync(s => s.UserId == userId, cancellationToken);
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
        video.TranscriptShortCuesJson = null;
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

    private static bool IsAudioContentType(string contentType) =>
        contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    private static IQueryable<UserVideo> ApplyV2Filter(IQueryable<UserVideo> query, bool includeV2)
    {
        return includeV2
            ? query
            : query.Where(v => v.TranscriptionVersion == null || v.TranscriptionVersion == 0);
    }

    private static IReadOnlyList<string>? ParseDialogueLines(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var lines = JsonSerializer.Deserialize<List<string>>(json, TranscriptJsonOptions)?
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();
            return lines is { Count: > 0 } ? lines : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<WordTimingDto>? ParseWordTiming(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var timings = JsonSerializer.Deserialize<List<WordTimingRecord>>(json, TranscriptJsonOptions)?
                .Where(t => !string.IsNullOrWhiteSpace(t.Word) && t.EndMs > t.StartMs)
                .Select(t => new WordTimingDto(t.Word, t.StartMs, t.EndMs, t.Confidence))
                .ToList();
            return timings is { Count: > 0 } ? timings : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

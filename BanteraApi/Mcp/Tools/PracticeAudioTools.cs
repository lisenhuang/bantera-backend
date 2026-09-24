using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Net;
using Amazon.S3;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Storage;
using BanteraApi.Videos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SixLabors.ImageSharp;

namespace BanteraApi.Mcp.Tools;

/// <summary>Imports a fully authored lesson through short-lived R2 uploads.</summary>
[McpServerToolType]
[Authorize(Policy = McpAuthDefaults.WritePolicy)]
public sealed class PracticeAudioTools(
    AppDbContext db,
    McpToolContext ctx,
    McpAuditLogger audit,
    R2StorageService storage,
    VideoService videos,
    ILogger<PracticeAudioTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan UploadWindow = TimeSpan.FromMinutes(15);

    [McpServerTool(Name = "begin_practice_audio_upload")]
    [Description("""
        Start an authored practice-audio submission. Returns two short-lived HTTPS PUT URLs:
        upload an MP3 or WAV to audioPutUrl with its matching Content-Type, and a JPEG cover
        to coverPutUrl with Content-Type: image/jpeg. Upload binary files directly with PUT;
        do not pass base64 through MCP. Then call submit_practice_audio with the uploadId.
        The upload is private until submission, and submission is private by default.
        """)]
    public async Task<string> BeginAsync(
        [Description("Audio file format: 'mp3' or 'wav'.")] string audioFormat,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();
        var extension = PracticeAudioImportValidator.AudioExtension(audioFormat);
        var id = Guid.NewGuid();
        var audioKey = StagingAudioKey(ctx.AdminUserId, id, extension);
        var coverKey = StagingCoverKey(ctx.AdminUserId, id);
        var audioUrl = storage.GeneratePresignedUploadUrl(audioKey, UploadWindow);
        var coverUrl = storage.GeneratePresignedUploadUrl(coverKey, UploadWindow);

        await audit.WriteAsync("begin_practice_audio_upload", new { uploadId = id, audioFormat },
            McpAuditOutcomes.Ok, "issued two 15-minute PUT URLs", ctx.AdminUserId,
            durationMs: (int)sw.ElapsedMilliseconds, ct: ct);

        return McpJson.Serialize(new
        {
            uploadId = id,
            expiresAt = DateTime.UtcNow.Add(UploadWindow),
            audioPutUrl = audioUrl,
            audioContentType = PracticeAudioImportValidator.AudioContentType(audioFormat),
            coverPutUrl = coverUrl,
            coverContentType = "image/jpeg",
            maxAudioBytes = PracticeAudioImportValidator.MaxAudioBytes,
            maxCoverBytes = PracticeAudioImportValidator.MaxCoverBytes,
            nextTool = "submit_practice_audio",
        });
    }

    [McpServerTool(Name = "submit_practice_audio")]
    [Description("""
        Validate and save a complete practice-audio lesson after both files have been PUT to
        the URLs from begin_practice_audio_upload. Each dialogue line must exactly match its
        indexed long transcript cue. Supply transcriptText explicitly; it must match the long
        cues. Word timings are required; optional short cues enable the short subtitle mode.
        Creates a private item unless isPublic is explicitly true. All content is supplied by
        the caller; this tool does not call Gemini or generate audio or a cover image.
        """)]
    public async Task<string> SubmitAsync(
        [Description("Upload ID returned by begin_practice_audio_upload.")] Guid uploadId,
        [Description("Same audio format used when beginning the upload: 'mp3' or 'wav'.")] string audioFormat,
        [Description("Lesson title, 1-180 characters.")] string title,
        [Description("Full language name, such as English or Cantonese.")] string transcriptLanguage,
        [Description("BCP-47 code, such as en-US or zh-HK.")] string transcriptLanguageCode,
        [Description("Complete transcript text, with one dialogue line per line, matching transcriptCues.")] string transcriptText,
        [Description("Actual audio duration in milliseconds, up to 30 minutes.")] int durationMs,
        [Description("Full dialogue lines in playback order, text only.")] string[] dialogueLines,
        [Description("One indexed long cue per dialogue line, with startMs, endMs and text.")] VideoTranscriptCue[] transcriptCues,
        [Description("Ordered word timing records with word, startMs, endMs, optional confidence and CJK parts.")] WordTimingRecord[] wordTiming,
        [Description("Optional indexed short subtitle cues.")] VideoTranscriptCue[]? transcriptShortCues = null,
        [Description("Publish to the shared library immediately. Default false for review.")] bool isPublic = false,
        CancellationToken ct = default)
    {
        ctx.RequireWrite();
        var sw = Stopwatch.StartNew();
        if (uploadId == Guid.Empty)
            throw new McpException("uploadId is required.");
        var extension = PracticeAudioImportValidator.AudioExtension(audioFormat);
        var transcript = PracticeAudioImportValidator.Validate(title, transcriptLanguage,
            transcriptLanguageCode, transcriptText, durationMs, dialogueLines, transcriptCues,
            transcriptShortCues, wordTiming);

        var ownerId = ctx.AdminUserId;
        var finalAudioKey = $"videos/{ownerId}/{uploadId:N}{extension}";
        var finalCoverKey = $"videos/{ownerId}/{uploadId:N}-cover.jpg";
        var existing = await db.UserVideos.AsNoTracking()
            .FirstOrDefaultAsync(v => v.MediaObjectKey == finalAudioKey, ct);
        if (existing is not null)
            return McpJson.Serialize(new { ok = true, alreadySubmitted = true, videoId = existing.Id,
                isPublic = existing.IsPublic });

        var stagedAudioKey = StagingAudioKey(ownerId, uploadId, extension);
        var stagedCoverKey = StagingCoverKey(ownerId, uploadId);
        StoredObjectResult audio;
        try
        {
            audio = await storage.DownloadObjectAsync(stagedAudioKey, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new McpException("Both audio and JPEG cover must be uploaded before submission.");
        }
        StoredObjectResult cover;
        try
        {
            cover = await storage.DownloadObjectAsync(stagedCoverKey, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            await audio.Stream.DisposeAsync();
            throw new McpException("Both audio and JPEG cover must be uploaded before submission.");
        }
        catch
        {
            await audio.Stream.DisposeAsync();
            throw;
        }

        var saveAttempted = false;
        try
        {
            await using (audio.Stream)
            await using (cover.Stream)
            {
                if (audio.ContentLength is < 128 or > PracticeAudioImportValidator.MaxAudioBytes
                    || !MimeMatches(audio.ContentType, PracticeAudioImportValidator.AudioContentType(audioFormat)))
                    throw new McpException("Audio size or Content-Type is invalid. Re-upload using the Content-Type returned by begin_practice_audio_upload.");
                if (cover.ContentLength is < 128 or > PracticeAudioImportValidator.MaxCoverBytes
                    || !MimeMatches(cover.ContentType, "image/jpeg"))
                    throw new McpException("JPEG cover size or Content-Type is invalid. Re-upload using Content-Type: image/jpeg.");

                var header = new byte[12];
                await audio.Stream.ReadExactlyAsync(header, ct);
                if (!LooksLikeAudio(header, extension))
                    throw new McpException("Uploaded audio does not match audioFormat.");

                await using var coverBytes = new MemoryStream();
                await cover.Stream.CopyToAsync(coverBytes, ct);
                if (coverBytes.Length < 3 || coverBytes.GetBuffer()[0] != 0xff
                    || coverBytes.GetBuffer()[1] != 0xd8 || coverBytes.GetBuffer()[2] != 0xff)
                    throw new McpException("The cover must be a JPEG image.");
                coverBytes.Position = 0;
                var coverInfo = await Image.IdentifyAsync(coverBytes, ct);
                if (coverInfo is null || coverInfo.Width is < 256 or > 4096
                    || coverInfo.Height is < 256 or > 4096)
                    throw new McpException("The cover image must be between 256 and 4096 pixels on each side.");
                coverBytes.Position = 0;
                using var coverImage = await Image.LoadAsync(coverBytes, ct);
                coverBytes.Position = 0;

                // Copy out of staging so a still-valid signed PUT URL cannot replace published media.
                var stagedAudio = await storage.DownloadObjectAsync(stagedAudioKey, ct);
                await using (stagedAudio.Stream)
                    await storage.UploadObjectAsync(finalAudioKey, stagedAudio.Stream,
                        PracticeAudioImportValidator.AudioContentType(audioFormat), ct);
                await storage.UploadObjectAsync(finalCoverKey, coverBytes, "image/jpeg", ct);
            }

            var now = DateTime.UtcNow;
            var video = new UserVideo
            {
                UserId = ownerId,
                MediaObjectKey = finalAudioKey,
                MediaContentType = PracticeAudioImportValidator.AudioContentType(audioFormat),
                OriginalFileName = $"{title.Trim()}{extension}",
                TranscriptText = transcript,
                TranscriptLanguage = transcriptLanguage.Trim(),
                TranscriptLanguageCode = transcriptLanguageCode.Trim(),
                TranscriptCuesJson = JsonSerializer.Serialize(transcriptCues, JsonOptions),
                TranscriptShortCuesJson = transcriptShortCues is { Length: > 0 }
                    ? JsonSerializer.Serialize(transcriptShortCues, JsonOptions) : null,
                TranscriptionVersion = 1,
                DialogueLinesJson = JsonSerializer.Serialize(dialogueLines, JsonOptions),
                WordTimingJson = JsonSerializer.Serialize(wordTiming, JsonOptions),
                IsPublic = isPublic,
                IsAiGenerated = true,
                IsTranscriptionEstimated = false,
                CoverImageObjectKey = finalCoverKey,
                FileSizeBytes = audio.ContentLength,
                DurationMs = durationMs,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.UserVideos.Add(video);
            saveAttempted = true;
            await db.SaveChangesAsync(ct);

            await audit.WriteAsync("submit_practice_audio", new
            {
                uploadId, audioFormat, title = title.Trim(), transcriptLanguageCode,
                durationMs, cueCount = transcriptCues.Length, wordCount = wordTiming.Length,
                isPublic,
            }, McpAuditOutcomes.Ok, $"created {video.Id}", ownerId, video.Id,
                (int)sw.ElapsedMilliseconds, ct);

            try
            {
                await storage.DeleteObjectAsync(stagedAudioKey, ct);
                await storage.DeleteObjectAsync(stagedCoverKey, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not clean staged practice audio {UploadId}", uploadId);
            }

            var response = await videos.GetVideoAsync(video.Id, ownerId,
                ctx.HttpContext ?? throw new McpException("No HTTP context is available."), ct);
            return McpJson.Serialize(new
            {
                ok = true,
                videoId = video.Id,
                response?.VideoUrl,
                response?.CoverImageUrl,
                response?.IsPublic,
                response?.TranscriptText,
                cueCount = transcriptCues.Length,
                shortCueCount = transcriptShortCues?.Length ?? 0,
                wordCount = wordTiming.Length,
            });
        }
        catch
        {
            // A failed SaveChanges acknowledgement can follow a committed insert. Keep the
            // media in that case so an existing database row never points at deleted files.
            if (!saveAttempted)
            {
                try
                {
                    await storage.DeleteObjectAsync(finalAudioKey, CancellationToken.None);
                    await storage.DeleteObjectAsync(finalCoverKey, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not clean failed practice audio {UploadId}", uploadId);
                }
            }
            throw;
        }
    }

    private static string StagingAudioKey(Guid ownerId, Guid id, string extension)
        => $"mcp/practice-audio/{ownerId}/{id:N}/audio{extension}";

    private static string StagingCoverKey(Guid ownerId, Guid id)
        => $"mcp/practice-audio/{ownerId}/{id:N}/cover.jpg";

    private static bool MimeMatches(string actual, string expected)
        => string.Equals(actual.Split(';')[0].Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeAudio(byte[] header, string extension)
        => extension == ".wav"
            ? header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WAVE"u8)
            : header.AsSpan(0, 3).SequenceEqual("ID3"u8)
              || (header[0] == 0xff && (header[1] & 0xe0) == 0xe0);
}

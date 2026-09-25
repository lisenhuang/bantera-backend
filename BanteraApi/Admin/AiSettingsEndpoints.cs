using System.Security.Claims;
using System.Text.RegularExpressions;
using BanteraApi.Auth;
using BanteraApi.Gemini;
using BanteraApi.Videos;
using Microsoft.Extensions.Options;

namespace BanteraApi.Admin;

/// <summary>
/// Lets admins pick the Gemini text and TTS models used for AI audio generation. The choices
/// come live from Gemini's model list, so new models appear without a deploy.
/// </summary>
public static partial class AiSettingsEndpoints
{
    [GeneratedRegex("^[a-z0-9][a-z0-9.\\-]{1,99}$")]
    private static partial Regex ModelNamePattern();

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/ai-settings").RequireAuthorization("Admin");

        // GET /api/admin/ai-settings — current models, defaults, and the live model lists.
        group.MapGet("", async (
            AiModelSettingsService settings,
            CueTimingSettingsService cueTiming,
            AiAudioAlignmentSettingsService alignmentSettings,
            GeminiService gemini,
            IOptions<GeminiSettings> geminiOptions,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var catalog = await TryListModelsAsync(gemini, loggerFactory, ct);
            return Results.Ok(await BuildResponseAsync(settings, cueTiming, alignmentSettings, geminiOptions.Value, catalog, ct));
        })
        .WithName("AdminGetAiSettings");

        // PUT /api/admin/ai-settings/playback — sentence start switch for AI audio.
        group.MapPut("/playback", async (
            UpdatePlaybackSettingsRequest req,
            ClaimsPrincipal user,
            CueTimingSettingsService cueTiming,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirst("sub")?.Value, out var adminId))
                return Results.Json(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."), statusCode: 401);

            await cueTiming.SetAsync(req.CueStartsAtPreviousCueEnd, adminId, ct);
            return Results.Ok(await BuildPlaybackAsync(cueTiming, ct));
        })
        .WithName("AdminUpdatePlaybackSettings");

        // PUT /api/admin/ai-settings/alignment — controls newly generated audio only.
        group.MapPut("/alignment", async (
            UpdateAlignmentSettingsRequest req,
            ClaimsPrincipal user,
            AiAudioAlignmentSettingsService alignmentSettings,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirst("sub")?.Value, out var adminId))
                return Results.Json(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."), statusCode: 401);

            await alignmentSettings.SetAsync(req.AlignToOriginalDialogue, adminId, ct);
            return Results.Ok(await BuildAlignmentAsync(alignmentSettings, ct));
        })
        .WithName("AdminUpdateAiAudioAlignmentSettings");

        // PUT /api/admin/ai-settings — set or clear (null / "") each override.
        group.MapPut("", async (
            UpdateAiSettingsRequest req,
            ClaimsPrincipal user,
            AiModelSettingsService settings,
            CueTimingSettingsService cueTiming,
            AiAudioAlignmentSettingsService alignmentSettings,
            GeminiService gemini,
            IOptions<GeminiSettings> geminiOptions,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirst("sub")?.Value, out var adminId))
                return Results.Json(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."), statusCode: 401);

            var textModel = Normalize(req.TextModel);
            var audioModel = Normalize(req.AudioModel);
            foreach (var model in new[] { textModel, audioModel })
            {
                if (model is not null && !ModelNamePattern().IsMatch(model))
                    return Results.BadRequest(new ApiError("invalid_model", $"\"{model}\" is not a valid model name."));
            }

            var catalog = await TryListModelsAsync(gemini, loggerFactory, ct);
            if (catalog is null)
                return Results.Json(new ApiError("model_list_unavailable", "Could not reach Gemini to confirm the models exist. Try again shortly."), statusCode: 503);
            if (textModel is not null && !catalog.TextModels.Contains(textModel))
                return Results.BadRequest(new ApiError("unknown_text_model", $"\"{textModel}\" is not an available text model."));
            if (audioModel is not null && !catalog.AudioModels.Contains(audioModel))
                return Results.BadRequest(new ApiError("unknown_audio_model", $"\"{audioModel}\" is not an available TTS model."));

            await settings.UpdateAsync(textModel, audioModel, adminId, ct);
            return Results.Ok(await BuildResponseAsync(settings, cueTiming, alignmentSettings, geminiOptions.Value, catalog, ct));
        })
        .WithName("AdminUpdateAiSettings");
    }

    private static string? Normalize(string? model)
    {
        var trimmed = model?.Trim().Replace("models/", "");
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static async Task<GeminiModelCatalog?> TryListModelsAsync(GeminiService gemini, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            return await gemini.ListModelsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggerFactory.CreateLogger("AiSettings").LogWarning(ex, "Could not list Gemini models.");
            return null;
        }
    }

    private static async Task<object> BuildPlaybackAsync(CueTimingSettingsService cueTiming, CancellationToken ct)
    {
        var row = await cueTiming.GetRowAsync(ct);
        return new
        {
            cueStartsAtPreviousCueEnd = row?.Value == "true",
            updatedAt = row?.UpdatedAt,
        };
    }

    private static async Task<object> BuildAlignmentAsync(AiAudioAlignmentSettingsService alignmentSettings, CancellationToken ct)
    {
        var row = await alignmentSettings.GetRowAsync(ct);
        return new
        {
            alignToOriginalDialogue = row?.Value != "false",
            updatedAt = row?.UpdatedAt,
        };
    }

    private static async Task<object> BuildResponseAsync(
        AiModelSettingsService settings,
        CueTimingSettingsService cueTiming,
        AiAudioAlignmentSettingsService alignmentSettings,
        GeminiSettings gemini,
        GeminiModelCatalog? catalog,
        CancellationToken ct)
    {
        var current = await settings.GetAsync(ct);
        var overrides = await settings.GetOverridesAsync(ct);
        object? Override(string key) => overrides.TryGetValue(key, out var row)
            ? new { value = row.Value, updatedAt = row.UpdatedAt, updatedByUserId = row.UpdatedByUserId }
            : null;

        return new
        {
            textModel = current.TextModel,
            audioModel = current.AudioModel,
            defaults = new { textModel = settings.Defaults.TextModel, audioModel = settings.Defaults.AudioModel },
            overrides = new
            {
                textModel = Override(AiModelSettingsService.TextModelKey),
                audioModel = Override(AiModelSettingsService.AudioModelKey),
            },
            fixedModels = new
            {
                webSearchModel = gemini.LatestNewsTextModel,
                webSearchKeyPrefix = gemini.WebSearchKeyPrefix,
                transcribeModel = gemini.TranscribeModel,
            },
            availableTextModels = catalog?.TextModels ?? [],
            availableAudioModels = catalog?.AudioModels ?? [],
            modelListAvailable = catalog is not null,
            playback = await BuildPlaybackAsync(cueTiming, ct),
            alignment = await BuildAlignmentAsync(alignmentSettings, ct),
        };
    }
}

public record UpdateAiSettingsRequest(string? TextModel, string? AudioModel);

public record UpdatePlaybackSettingsRequest(bool CueStartsAtPreviousCueEnd);

public record UpdateAlignmentSettingsRequest(bool AlignToOriginalDialogue);

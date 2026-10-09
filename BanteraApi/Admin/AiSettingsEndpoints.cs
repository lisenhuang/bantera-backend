using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using BanteraApi.Auth;
using BanteraApi.OpenAi;
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

    [GeneratedRegex("^[a-f0-9]{64}$")]
    private static partial Regex KeyIdPattern();

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/ai-settings").RequireAuthorization("Admin");

        // GET /api/admin/ai-settings — current models, defaults, and the live model lists.
        group.MapGet("", async (
            AiModelSettingsService settings,
            CueTimingSettingsService cueTiming,
            AiAudioAlignmentSettingsService alignmentSettings,
            GeminiService gemini,
            ChatGptSubscriptionClient chatGpt,
            IOptions<GeminiSettings> geminiOptions,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var catalog = await TryListModelsAsync(gemini, loggerFactory, ct);
            var gpt = await TryListGptAsync(chatGpt, ct);
            return Results.Ok(await BuildResponseAsync(settings, cueTiming, alignmentSettings, geminiOptions.Value, catalog, gpt, ct));
        })
        .WithName("AdminGetAiSettings");

        group.MapGet("/keys", async (GeminiKeyHealthService keyHealth, CancellationToken ct) =>
            Results.Ok(await keyHealth.GetSnapshotAsync(ct)))
            .WithName("AdminGetGeminiKeyHealth");

        group.MapPost("/keys/{id}/retry", async (
            string id,
            GeminiKeyHealthService keyHealth,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!KeyIdPattern().IsMatch(id) || !await keyHealth.ReenableAsync(id, ct))
                return Results.NotFound();
            loggerFactory.CreateLogger("AiSettings").LogInformation(
                "Admin re-enabled Gemini key with fingerprint {KeyId}.", id);
            return Results.Ok(await keyHealth.GetSnapshotAsync(ct));
        })
        .WithName("AdminRetryGeminiKey");

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
            ChatGptSubscriptionClient chatGpt,
            IOptions<GeminiSettings> geminiOptions,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirst("sub")?.Value, out var adminId))
                return Results.Json(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."), statusCode: 401);

            if (!AiRequestTimeout.TryRead(req.GptTimeoutSeconds, out var gptTimeoutSeconds))
                return Results.BadRequest(new ApiError("invalid_timeout", "GPT response timeout must be a whole number from 30 to 300 seconds."));
            var textModel = Normalize(req.TextModel);
            var audioModel = Normalize(req.AudioModel);
            if (!TryReadOptionalModel(req.FallbackTextModel, out var fallbackTextModel) ||
                !TryReadOptionalModel(req.FallbackAudioModel, out var fallbackAudioModel))
                return Results.BadRequest(new ApiError("invalid_model", "Fallback models must be model names or null."));
            foreach (var model in new[] { textModel, audioModel, fallbackTextModel, fallbackAudioModel })
            {
                if (model is not null && !ModelNamePattern().IsMatch(model.StartsWith("chatgpt/", StringComparison.Ordinal) ? model[8..] : model))
                    return Results.BadRequest(new ApiError("invalid_model", $"\"{model}\" is not a valid model name."));
            }

            var catalog = await TryListModelsAsync(gemini, loggerFactory, ct);
            var gpt = await TryListGptAsync(chatGpt, ct);
            if (catalog is null)
                return Results.Json(new ApiError("model_list_unavailable", "Could not reach Gemini to confirm the models exist. Try again shortly."), statusCode: 503);
            if (textModel is not null && !catalog.TextModels.Concat(gpt?.Select(m => "chatgpt/" + m.Id) ?? []).Contains(textModel))
                return Results.BadRequest(new ApiError("unknown_text_model", $"\"{textModel}\" is not an available text model."));
            if (audioModel is not null && !catalog.AudioModels.Contains(audioModel))
                return Results.BadRequest(new ApiError("unknown_audio_model", $"\"{audioModel}\" is not an available TTS model."));
            if (fallbackTextModel is not null && !catalog.TextModels.Concat(gpt?.Select(m => "chatgpt/" + m.Id) ?? []).Contains(fallbackTextModel))
                return Results.BadRequest(new ApiError("unknown_text_model", $"\"{fallbackTextModel}\" is not an available text model."));
            if (fallbackAudioModel is not null && !catalog.AudioModels.Contains(fallbackAudioModel))
                return Results.BadRequest(new ApiError("unknown_audio_model", $"\"{fallbackAudioModel}\" is not an available TTS model."));

            var current = await settings.GetAsync(ct);
            var effectiveFallbackTextModel = req.FallbackTextModel.ValueKind == JsonValueKind.Undefined
                ? current.FallbackTextModel : fallbackTextModel;
            var effectiveFallbackAudioModel = req.FallbackAudioModel.ValueKind == JsonValueKind.Undefined
                ? current.FallbackAudioModel : fallbackAudioModel;
            if ((effectiveFallbackTextModel is not null && effectiveFallbackTextModel == (textModel ?? settings.Defaults.TextModel)) ||
                (effectiveFallbackAudioModel is not null && effectiveFallbackAudioModel == (audioModel ?? settings.Defaults.AudioModel)))
                return Results.BadRequest(new ApiError("duplicate_fallback_model", "A fallback model must differ from its primary model."));

            if (!TryReadOptionalModel(req.TextReasoning, out var textReasoning) || !TryReadOptionalModel(req.FallbackTextReasoning, out var fallbackReasoning))
                return Results.BadRequest(new ApiError("invalid_reasoning", "Reasoning must be a supported level or null."));
            // Old dashboards omit reasoning: retain it only when the corresponding model is unchanged.
            textReasoning = req.TextReasoning.ValueKind == JsonValueKind.Undefined && current.TextModel == (textModel ?? settings.Defaults.TextModel)
                ? current.TextReasoning : textReasoning;
            fallbackReasoning = req.FallbackTextReasoning.ValueKind == JsonValueKind.Undefined && current.FallbackTextModel == effectiveFallbackTextModel
                ? current.FallbackTextReasoning : fallbackReasoning;
            if (!ValidReasoning(textModel ?? settings.Defaults.TextModel, textReasoning, gpt) || !ValidReasoning(effectiveFallbackTextModel, fallbackReasoning, gpt))
                return Results.BadRequest(new ApiError("invalid_reasoning", "Choose a reasoning level supported by each selected GPT model, or model default."));

            var updateSearch = req.SearchModel.ValueKind != JsonValueKind.Undefined || req.FallbackSearchModel.ValueKind != JsonValueKind.Undefined ||
                req.SearchReasoning.ValueKind != JsonValueKind.Undefined || req.FallbackSearchReasoning.ValueKind != JsonValueKind.Undefined;
            if (!TryReadOptionalModel(req.SearchModel, out var searchModel) || !TryReadOptionalModel(req.FallbackSearchModel, out var fallbackSearchModel) ||
                !TryReadOptionalModel(req.SearchReasoning, out var searchReasoning) || !TryReadOptionalModel(req.FallbackSearchReasoning, out var fallbackSearchReasoning))
                return Results.BadRequest(new ApiError("invalid_search_model", "Search settings must be supported models and reasoning levels."));
            searchModel = req.SearchModel.ValueKind == JsonValueKind.Undefined ? current.SearchModel : searchModel ?? settings.Defaults.SearchModel;
            fallbackSearchModel = req.FallbackSearchModel.ValueKind == JsonValueKind.Undefined ? current.FallbackSearchModel : fallbackSearchModel;
            searchReasoning = req.SearchReasoning.ValueKind == JsonValueKind.Undefined && searchModel == current.SearchModel ? current.SearchReasoning : searchReasoning;
            fallbackSearchReasoning = req.FallbackSearchReasoning.ValueKind == JsonValueKind.Undefined && fallbackSearchModel == current.FallbackSearchModel ? current.FallbackSearchReasoning : fallbackSearchReasoning;
            if (updateSearch) {
                var available = catalog.TextModels.Concat(gpt?.Select(m => "chatgpt/" + m.Id) ?? []).Where(AiSearchPolicy.AllowsModel).ToHashSet();
                if (searchModel is null || !available.Contains(searchModel) || fallbackSearchModel is not null && !available.Contains(fallbackSearchModel))
                    return Results.BadRequest(new ApiError("invalid_search_model", "Choose Gemini 2.5 Flash or an available GPT search model. Other Gemini models cannot be used for search."));
                if (searchModel == fallbackSearchModel) return Results.BadRequest(new ApiError("duplicate_fallback_model", "The fallback search model must differ from its primary model."));
                if (!ValidReasoning(searchModel, searchReasoning, gpt) || !ValidReasoning(fallbackSearchModel, fallbackSearchReasoning, gpt))
                    return Results.BadRequest(new ApiError("invalid_reasoning", "Choose supported reasoning levels for your search models."));
            }

            await settings.UpdateAsync(textModel, audioModel, adminId, ct,
                updateFallbackTextModel: req.FallbackTextModel.ValueKind != JsonValueKind.Undefined,
                fallbackTextModel: fallbackTextModel,
                updateFallbackAudioModel: req.FallbackAudioModel.ValueKind != JsonValueKind.Undefined,
                fallbackAudioModel: fallbackAudioModel,
                updateReasoning: true, textReasoning: textReasoning, fallbackTextReasoning: fallbackReasoning,
                updateSearch: updateSearch, searchModel: searchModel, fallbackSearchModel: fallbackSearchModel,
                searchReasoning: searchReasoning, fallbackSearchReasoning: fallbackSearchReasoning,
                updateGptTimeout: req.GptTimeoutSeconds.ValueKind != JsonValueKind.Undefined, gptTimeoutSeconds: gptTimeoutSeconds);
            return Results.Ok(await BuildResponseAsync(settings, cueTiming, alignmentSettings, geminiOptions.Value, catalog, gpt, ct));
        })
        .WithName("AdminUpdateAiSettings");
    }

    public static bool ValidReasoning(string? model, string? reasoning, IReadOnlyList<ChatGptModel>? models) =>
        string.IsNullOrEmpty(reasoning) || model?.StartsWith("chatgpt/", StringComparison.Ordinal) == true &&
        models?.Any(m => "chatgpt/" + m.Id == model && m.ReasoningLevels.Contains(reasoning)) == true;

    private static async Task<IReadOnlyList<ChatGptModel>?> TryListGptAsync(ChatGptSubscriptionClient service, CancellationToken ct) {
        try { return await service.ModelsAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
    }

    private static string? Normalize(string? model)
    {
        var trimmed = model?.Trim().Replace("models/", "");
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static bool TryReadOptionalModel(JsonElement value, out string? model)
    {
        model = null;
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        model = Normalize(value.GetString());
        return true;
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
        IReadOnlyList<ChatGptModel>? gpt,
        CancellationToken ct)
    {
        var current = await settings.GetAsync(ct);
        var overrides = await settings.GetOverridesAsync(ct);
        object? Override(string key) => overrides.TryGetValue(key, out var row)
            ? new { value = row.Value, updatedAt = row.UpdatedAt, updatedByUserId = row.UpdatedByUserId }
            : null;

        return new
        {
            gptTimeoutSeconds = current.GptTimeoutSeconds,
            textModel = current.TextModel,
            audioModel = current.AudioModel,
            fallbackTextModel = current.FallbackTextModel,
            fallbackAudioModel = current.FallbackAudioModel,
            textReasoning = current.TextReasoning,
            fallbackTextReasoning = current.FallbackTextReasoning,
            searchModel = current.SearchModel, fallbackSearchModel = current.FallbackSearchModel,
            searchReasoning = current.SearchReasoning, fallbackSearchReasoning = current.FallbackSearchReasoning,
            defaults = new { textModel = settings.Defaults.TextModel, audioModel = settings.Defaults.AudioModel, searchModel = settings.Defaults.SearchModel },
            overrides = new
            {
                textModel = Override(AiModelSettingsService.TextModelKey),
                audioModel = Override(AiModelSettingsService.AudioModelKey),
                fallbackTextModel = Override(AiModelSettingsService.FallbackTextModelKey),
                fallbackAudioModel = Override(AiModelSettingsService.FallbackAudioModelKey),
            },
            fixedModels = new
            {
                webSearchModel = current.SearchModel ?? AiSearchPolicy.GeminiModel,
                webSearchKeyPrefix = AiSearchPolicy.GeminiKeyPrefix,
                transcribeModel = gemini.TranscribeModel,
            },
            availableTextModels = (catalog?.TextModels ?? []).Concat(gpt?.Select(m => "chatgpt/" + m.Id) ?? []).ToArray(),
            gptModelListAvailable = gpt is not null,
            textModelOptions = (catalog?.TextModels ?? []).Select(m => new { id = m, name = m, provider = "Gemini", reasoningLevels = Array.Empty<string>(), defaultReasoning = (string?)null })
                .Concat((gpt ?? []).Select(m => new { id = "chatgpt/" + m.Id, name = m.Name, provider = "ChatGPT", reasoningLevels = m.ReasoningLevels, defaultReasoning = m.DefaultReasoning })),
            searchModelOptions = (catalog?.TextModels ?? []).Where(m => m == AiSearchPolicy.GeminiModel)
                .Select(m => new { id = m, name = "Gemini 2.5 Flash", provider = "Gemini", reasoningLevels = Array.Empty<string>(), defaultReasoning = (string?)null })
                .Concat((gpt ?? []).Select(m => new { id = "chatgpt/" + m.Id, name = m.Name, provider = "ChatGPT", reasoningLevels = m.ReasoningLevels, defaultReasoning = m.DefaultReasoning })),
            availableAudioModels = catalog?.AudioModels ?? [],
            modelListAvailable = catalog is not null,
            playback = await BuildPlaybackAsync(cueTiming, ct),
            alignment = await BuildAlignmentAsync(alignmentSettings, ct),
        };
    }
}

public record UpdateAiSettingsRequest(string? TextModel, string? AudioModel, JsonElement FallbackTextModel = default, JsonElement FallbackAudioModel = default, JsonElement TextReasoning = default, JsonElement FallbackTextReasoning = default, JsonElement SearchModel = default, JsonElement FallbackSearchModel = default, JsonElement SearchReasoning = default, JsonElement FallbackSearchReasoning = default, JsonElement GptTimeoutSeconds = default);

public record UpdatePlaybackSettingsRequest(bool CueStartsAtPreviousCueEnd);

public record UpdateAlignmentSettingsRequest(bool AlignToOriginalDialogue);

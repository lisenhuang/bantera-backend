namespace BanteraApi.Gemini;

// Product restriction for server-side lesson searches. Device search is separate.
public static class AiSearchPolicy
{
    public const string GeminiModel = "gemini-2.5-flash";
    public const string GeminiKeyPrefix = "AIzaSy";
    public static bool AllowsModel(string? model) => model == GeminiModel ||
        model is not null && GeminiService.IsChatGpt(model);

    // Do not execute a Gemini choice saved before this restriction was introduced.
    public static AiModelSelection Apply(AiModelSelection selection)
    {
        var primary = AllowsModel(selection.SearchModel) ? selection.SearchModel! : GeminiModel;
        var fallback = AllowsModel(selection.FallbackSearchModel) && selection.FallbackSearchModel != primary
            ? selection.FallbackSearchModel : null;
        return selection with {
            SearchModel = primary, FallbackSearchModel = fallback,
            SearchReasoning = GeminiService.IsChatGpt(primary) ? selection.SearchReasoning : null,
            FallbackSearchReasoning = fallback is not null && GeminiService.IsChatGpt(fallback) ? selection.FallbackSearchReasoning : null
        };
    }
}

using System.Diagnostics;
using BanteraApi.Auth;
using BanteraApi.Diagnostics;
using BanteraApi.Gemini;
using Microsoft.Extensions.Options;

namespace BanteraApi.Admin;

public static class AiSearchTestEndpoint
{
    public sealed record Request(string? Query);
    public static bool Valid(Request request) => request.Query?.Trim().Length is >= 3 and <= 1000;

    public static void Map(WebApplication app) => app.MapPost("/api/admin/ai-settings/search-test", async (
        Request request, GeminiService gemini, IOptions<GeminiSettings> settings, AiModelSettingsService modelSettings,
        AiPipelineEventRecorder events, ILoggerFactory loggers, HttpContext context, CancellationToken ct) => {
        if (!Valid(request)) return Results.BadRequest(new ApiError("invalid_query", "Enter a search query between 3 and 1,000 characters."));
        var id = Guid.NewGuid();
        var clock = Stopwatch.StartNew();
        var selectedSettings = await modelSettings.GetAsync(ct);
        var model = selectedSettings.SearchModel ?? AiSearchPolicy.GeminiModel;
        using var eventContext = events.BeginContext(Guid.TryParse(context.User.FindFirst("sub")?.Value, out var admin) ? admin : null,
            null, null, "/api/admin/ai-settings/search-test");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(AiRequestTimeout.SearchTestBudgetSeconds(selectedSettings.GptTimeoutSeconds)));
        try {
            var answer = await gemini.TestWebSearchAsync(request.Query!.Trim(), timeout.Token);
            var usedFallback = answer.ActualModel is not null && answer.ActualModel != model;
            model = answer.ActualModel ?? model;
            await events.RecordAsync(answer.Verified ? AiPipelineSeverity.Info : AiPipelineSeverity.Warning, "admin_web_search",
                answer.Verified ? "search_test_passed" : "search_test_unverified", "Admin web search test finished.",
                new { testId = id, verified = answer.Verified, sourceCount = answer.Sources.Count }, model, durationMs: (int)clock.ElapsedMilliseconds);
            return Results.Ok(new { success = answer.Verified, code = answer.Verified ? "verified" : "search_not_verified",
                message = answer.Verified ? "Web search verified." : "The model replied, but web search could not be verified.",
                provider = GeminiService.IsChatGpt(model) ? "ChatGPT" : "Gemini", model, usedFallback, testId = id, durationMs = clock.ElapsedMilliseconds,
                answer = answer.Text, sources = answer.Sources, queries = answer.Queries, suggestionsHtml = answer.SuggestionsHtml });
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) {
            var code = timeout.IsCancellationRequested ? "timeout" : "provider_failed";
            for (Exception? cause = ex; cause is not null; cause = cause.InnerException) {
                if (cause is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }) code = "quota_limited";
                if (cause is TimeoutException) code = "timeout";
                if (cause is InvalidDataException) code = "invalid_response";
            }
            loggers.CreateLogger("AiSearchTest").LogWarning("Search test {TestId} failed: {Code}, {ExceptionType}", id, code, ex.GetType().Name);
            await events.RecordAsync(AiPipelineSeverity.Warning, "admin_web_search", "search_test_failed", "Admin web search test failed.",
                new { testId = id, code, exceptionType = ex.GetType().Name }, model, durationMs: (int)clock.ElapsedMilliseconds);
            return Results.Ok(new { success = false, code, message = code == "timeout" ? "Search timed out. Try again." : "Search could not complete. Check the diagnostic reference in pipeline events.",
                provider = GeminiService.IsChatGpt(model) ? "ChatGPT" : "Gemini", model, testId = id, durationMs = clock.ElapsedMilliseconds });
        }
    }).RequireAuthorization("Admin").RequireRateLimiting("admin-search-test")
      .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(8192)).WithName("AdminTestWebSearch");
}

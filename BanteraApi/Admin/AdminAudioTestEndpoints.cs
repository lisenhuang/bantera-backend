using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Gemini;
using BanteraApi.Storage;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Admin;

public static partial class AdminAudioTestEndpoints
{
    [GeneratedRegex("^[a-z0-9][a-z0-9.\\-]{1,99}$")]
    private static partial Regex ModelPattern();

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/audio-tests").RequireAuthorization("Admin");
        group.MapGet("", async (AppDbContext db, int? offset, CancellationToken ct) =>
        {
            var skip = Math.Max(0, offset ?? 0);
            var query = db.AdminAudioTests.AsNoTracking();
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(t => t.CreatedAt).Skip(skip).Take(20)
                .Select(t => new { t.Id, t.Status, t.Stage, t.LanguageCode, t.Language,
                    t.TargetDurationSeconds, t.TextModel, t.AudioModel, t.Title, t.SourceTestId,
                    t.AudioDurationMs, t.CreatedAt, t.StartedAt, t.CompletedAt,
                    hasAudio = t.AudioObjectKey != null, hasDialogue = t.DialogueJson != null })
                .ToListAsync(ct);
            return Results.Ok(new { total, items });
        });
        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var test = await db.AdminAudioTests.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);
            return test is null ? Results.NotFound() : Results.Ok(Detail(test));
        });
        group.MapPost("", async (CreateAudioTestRequest request, ClaimsPrincipal user,
            AppDbContext db, AiModelSettingsService settings, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirst("sub")?.Value, out var adminId))
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(request.AudioModel) || !ModelPattern().IsMatch(request.AudioModel))
                return Results.BadRequest(new { error = "Choose a valid TTS model." });
            if (await db.AdminAudioTests.CountAsync(t => t.CreatedByUserId == adminId &&
                (t.Status == "queued" || t.Status == "running"), ct) >= 5)
                return Results.Json(new { error = "You already have five pending tests. Wait for one to finish." }, statusCode: 429);
            var models = await settings.GetAsync(ct);
            var test = new AdminAudioTest { CreatedByUserId = adminId,
                AudioModel = request.AudioModel, TextModel = models.TextModel };
            if (request.SourceTestId is { } sourceId)
            {
                var source = await db.AdminAudioTests.AsNoTracking().SingleOrDefaultAsync(t => t.Id == sourceId, ct);
                if (source?.DialogueJson is null)
                    return Results.BadRequest(new { error = "This test has no saved dialogue to reuse." });
                test.SourceTestId = source.Id;
                test.DialogueJson = source.DialogueJson;
                test.Title = source.Title;
                test.LanguageCode = source.LanguageCode;
                test.Language = source.Language;
                test.TargetDurationSeconds = source.TargetDurationSeconds;
                test.TextModel = source.TextModel;
            }
            else
            {
                var language = LearningLanguageCatalog.Items.FirstOrDefault(l => l.Identifier == request.LanguageCode);
                if (language is null || request.DurationSeconds is not (60 or 120 or 180 or 240))
                    return Results.BadRequest(new { error = "Choose a language and a duration from 1 to 4 minutes." });
                test.LanguageCode = language.Identifier;
                test.Language = language.DisplayName;
                test.TargetDurationSeconds = request.DurationSeconds;
            }
            db.AdminAudioTests.Add(test);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/admin/audio-tests/{test.Id}", Detail(test));
        });
        group.MapGet("/{id:guid}/audio", async (Guid id, AppDbContext db, R2StorageService storage,
            HttpContext context, CancellationToken ct) =>
        {
            var test = await db.AdminAudioTests.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);
            if (test?.AudioObjectKey is null || test.Status != "done") return Results.NotFound();
            var audio = await storage.DownloadObjectAsync(test.AudioObjectKey, ct);
            using var stream = audio.Stream;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.File(buffer.ToArray(), test.AudioContentType ?? "audio/mpeg", enableRangeProcessing: true);
        });
    }

    private static object Detail(AdminAudioTest t) => new
    {
        t.Id, t.Status, t.Stage, t.LanguageCode, t.Language, t.TargetDurationSeconds,
        t.TextModel, t.AudioModel, t.Title, t.SourceTestId, t.AudioDurationMs, t.AudioBytes,
        t.CreatedAt, t.StartedAt, t.CompletedAt,
        hasAudio = t.AudioObjectKey != null, hasDialogue = t.DialogueJson != null,
        dialogue = Parse(t.DialogueJson), diagnostics = Parse(t.DiagnosticsJson), error = Parse(t.ErrorJson),
    };

    private static JsonElement? Parse(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);

    public sealed record CreateAudioTestRequest(string LanguageCode, int DurationSeconds, string AudioModel, Guid? SourceTestId);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Chat.Ai;

public static class AiBugReportTool
{
    public const string Name = "submit_bug_report";
    public const string Prompt = " Bug reports: you CAN submit a Bantera platform issue to the developer dashboard using submit_bug_report in voice messages, text chats, in-app live calls and CallKit callbacks. " +
        "When the learner describes a Bantera bug, briefly summarise what would be reported and ask whether to send that summary with their account identity to the developer team. Wait for a clear affirmative reply in THIS conversation before calling the tool. " +
        "A complaint, silence, uncertain speech, a quoted example, old consent or your own earlier promise is NOT consent. If they decline, do not submit or repeatedly ask. Ask only for missing facts needed to describe the problem; do not demand technical details. " +
        "Only include the agreed issue, observed behaviour and expected behaviour/steps if supplied. Do not invent a cause, collect passwords/tokens, or send unrelated personal conversation. Preserve the learner's language. " +
        "Set userConfirmed true only after that explicit permission; quote their actual confirmation in consentQuote. Identity, channel and submission time are assigned by the server, not by you. " +
        "Only say the report was submitted after THIS tool returns submitted=true and a reportId. On failure say it was not confirmed submitted and offer to retry or direct them to https://bantera.app/support or contact@bantera.app. " +
        "Do not claim anyone read it, will contact them, or has fixed it; do not promise a response or repair time. Do not invent report IDs or claim old issues were automatically forwarded. " +
        "Explain that only the consented issue summary and confirmation are saved for developer review, not the whole call or chat. You cannot submit reports to other companies or send email on the learner's behalf. ";
    public static readonly string[] Categories = ["audio", "calls", "practice", "translation", "vocabulary", "account", "payments", "website", "other"];
    public static object Declaration => new {
        name = Name,
        description = "Submit a Bantera bug to its developer dashboard ONLY after summarising it, explaining that the account identity is included, asking permission, and receiving explicit confirmation. Never call merely because a bug is mentioned. Confirm submission only on submitted=true with a reportId.",
        parameters = new { type = "OBJECT", properties = new {
            summary = new { type = "STRING", description = "Agreed issue title, 1 to 160 characters." },
            details = new { type = "STRING", description = "Agreed description, 1 to 4000 characters. Only supplied facts about the Bantera issue; no unrelated conversation or secrets." },
            category = new { type = "STRING", @enum = Categories },
            userConfirmed = new { type = "BOOLEAN", description = "True only for explicit permission after the report summary and account-identity disclosure." },
            consentQuote = new { type = "STRING", description = "The learner's actual affirmative reply, 1 to 500 characters. Never invent or quote older consent." }
        }, required = new[] { "summary", "details", "category", "userConfirmed", "consentQuote" } }
    };
    public sealed record Input(string Summary, string Details, string Category, string ConsentQuote);
    public static Input? Read(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("userConfirmed", out var consent) || consent.ValueKind != JsonValueKind.True) return null;
        string? Text(string name, int max) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()?.Trim() is { Length: > 0 } text && text.Length <= max ? text : null;
        var summary = Text("summary", 160); var details = Text("details", 4000);
        var category = Text("category", 32); var quote = Text("consentQuote", 500);
        return summary is not null && details is not null && quote is not null && Categories.Contains(category)
            ? new(summary, details, category!, quote) : null;
    }
    public static string Source(AiClientMetadata meta) => meta.PhoneCallback ? "callkit" : meta.ReportSource;
    public static async Task<object> ExecuteAsync(AppDbContext db, Guid userId, AiClientMetadata meta, JsonElement args, CancellationToken ct)
    {
        var input = Read(args);
        if (input is null) return new { submitted = false, code = "confirmation_or_details_required", error = "Nothing was submitted. Summarise the Bantera issue, explain that account identity is included, and obtain explicit permission before submitting valid details." };
        var now = DateTime.UtcNow;
        // Deduplicate retries and reconnections with the same agreed content on the same UTC day.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { date = now.ToString("yyyy-MM-dd"), input.Summary, input.Details }))));
        var existing = await db.AiBugReports.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.DedupeKey == key, ct);
        if (existing is not null) return Receipt(existing, true);
        if (!await db.Users.AnyAsync(x => x.Id == userId && x.DeletedAt == null && x.Status == "active", ct))
            return new { submitted = false, code = "account_unavailable" };
        if (await db.AiBugReports.CountAsync(x => x.UserId == userId && x.CreatedAt >= now.AddDays(-1), ct) >= 20)
            return new { submitted = false, code = "daily_limit", error = "The report limit was reached. Please use bantera.app/support for further issues." };
        var report = new AiBugReport { Id = Guid.NewGuid(), UserId = userId, Summary = input.Summary, Details = input.Details,
            Category = input.Category, ConsentQuote = input.ConsentQuote, Source = Source(meta), CreatedAt = now, DedupeKey = key };
        db.AiBugReports.Add(report);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" }) {
            db.Entry(report).State = EntityState.Detached;
            existing = await db.AiBugReports.AsNoTracking().SingleAsync(x => x.UserId == userId && x.DedupeKey == key, ct);
            return Receipt(existing, true);
        }
        return Receipt(report, false);
    }
    private static object Receipt(AiBugReport report, bool alreadySubmitted) => new {
        submitted = true, reportId = report.Id, submittedAt = report.CreatedAt, alreadySubmitted,
        message = "Saved to the developer dashboard. This does not mean it has been reviewed or fixed."
    };
}

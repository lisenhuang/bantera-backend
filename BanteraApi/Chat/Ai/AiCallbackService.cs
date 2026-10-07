using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BanteraApi.Chat.Ai;

public sealed class AiCallbackService(AppDbContext db, IOptions<ApnsSettings> apns)
{
    public const string Identity = "ba07e2a0-a100-4000-8000-000000000001";
    public static readonly string[] ToolNames = ["get_current_time", "schedule_callback", "schedule_reminder", "list_callbacks", "cancel_callback"];
    public static object[] Declarations => [
        new { name = "get_current_time", description = "Get trusted current time in the learner's timezone before interpreting an absolute callback time.", parameters = new { type = "OBJECT", properties = new { } } },
        new { name = "schedule_callback", description = "ONLY when the learner explicitly asks you to call them later AND has told you what to remind them about. If they only give a time, ask what to remind them about and wait for their answer; do not schedule yet. Do not invent a reminder or assume language practice. Use delaySeconds for relative requests, e.g. 60 for one minute, OR atUtc for an explicit time converted using get_current_time. Ask if ambiguous. Confirm only when the tool succeeds. The server stores the schedule and short reminder; delivery needs connectivity and this iOS device's call notifications. Never promise exact delivery.", parameters = new { type = "OBJECT", properties = new { explicitCallRequested = new { type = "BOOLEAN", description = "True ONLY when the learner explicitly requested a phone/audio call, not just a reminder." }, reminder = new { type = "STRING", description = "The reminder requested by the learner, 1 to 500 characters. Required; never invent it." }, delaySeconds = new { type = "INTEGER", description = "Seconds from now; 10 to 2592000." }, atUtc = new { type = "STRING", description = "ISO 8601 UTC timestamp, alternative to delaySeconds." } }, required = new[] { "reminder", "explicitCallRequested" } } },
        new { name = "schedule_reminder", description = "Default for requests to remind the learner at a time: deliver a VOICE MESSAGE, never ring or call. Ask for missing reminder topic or ambiguous time. Use delaySeconds OR atUtc with get_current_time. Confirm only on tool success. Audio is held temporarily for delivery, not permanent chat history.", parameters = new { type = "OBJECT", properties = new { reminder = new { type = "STRING", description = "The learner's reminder, 1 to 500 characters." }, delaySeconds = new { type = "INTEGER" }, atUtc = new { type = "STRING" } }, required = new[] { "reminder" } } },
        new { name = "list_callbacks", description = "List this learner's pending reminders and calls, including delivery type, with server IDs for cancellation.", parameters = new { type = "OBJECT", properties = new { } } },
        new { name = "cancel_callback", description = "Cancel a pending reminder or call only when requested. Get its ID from list_callbacks.", parameters = new { type = "OBJECT", properties = new { id = new { type = "STRING" } }, required = new[] { "id" } } }
    ];
    public static string? ReadReminder(JsonElement args)
    {
        if (!args.TryGetProperty("reminder", out var value) || value.ValueKind != JsonValueKind.String) return null;
        var reminder = value.GetString()?.Trim();
        return reminder is { Length: > 0 and <= 500 } ? reminder : null;
    }
    public static async Task<string?> ReminderForCallAsync(AppDbContext db, Guid user, Guid callbackId, CancellationToken ct) =>
        await db.AiCallbacks.AsNoTracking()
            .Where(c => c.Id == callbackId && c.UserId == user && c.Status == "answered")
            .Select(c => c.Reminder).SingleOrDefaultAsync(ct);

    public static DateTime? ResolveDue(JsonElement args, DateTime now)
    {
        if (args.TryGetProperty("delaySeconds", out var delay) && !args.TryGetProperty("atUtc", out _) && delay.TryGetInt32(out var seconds))
            return seconds is >= 10 and <= 2592000 ? now.AddSeconds(seconds) : null;
        if (args.TryGetProperty("atUtc", out var at) && !args.TryGetProperty("delaySeconds", out _) && at.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var due) &&
            (at.GetString()!.EndsWith('Z') || at.GetString()!.Contains('+')) && due.UtcDateTime >= now.AddSeconds(10) && due.UtcDateTime <= now.AddDays(30)) return due.UtcDateTime;
        return null;
    }
    public async Task<object> ExecuteAsync(Guid user, AiClientMetadata meta, string requestKey, string name, JsonElement args, CancellationToken ct)
    {
        if (name == "get_current_time") return AiClientMetadata.CurrentTime(meta.Clock);
        if (name == "list_callbacks") return new { callbacks = await db.AiCallbacks.AsNoTracking().Where(c => c.UserId == user && (c.Status == "scheduled" || c.Status == "queued" || c.Status == "generating")).OrderBy(c => c.DueAt)
            .Select(c => new { c.Id, c.DueAt, c.TimeZone, c.Reminder, c.Delivery }).Take(20).ToListAsync(ct) };
        if (name == "cancel_callback") {
            if (!args.TryGetProperty("id", out var value) || !Guid.TryParse(value.GetString(), out var id)) return new { error = "Invalid callback." };
            var changed = await db.AiCallbacks.Where(c => c.UserId == user && c.Id == id && (c.Status == "scheduled" || c.Status == "queued" || c.Status == "ringing" || c.Status == "generating" || c.Status == "ready"))
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, "cancelled").SetProperty(c => c.Audio, (byte[]?)null).SetProperty(c => c.Transcript, (string?)null), ct);
            return new { cancelled = changed == 1 };
        }
        if (name is not ("schedule_callback" or "schedule_reminder")) return new { unavailable = true };
        var isCall = name == "schedule_callback";
        if (isCall && (!args.TryGetProperty("explicitCallRequested", out var explicitCall) || explicitCall.ValueKind != JsonValueKind.True))
            return new { error = "Do not call for a reminder-only request. Use schedule_reminder unless the learner explicitly requested a call.", needsCallRequest = true };
        var reminder = ReadReminder(args);
        if (reminder is null) return new { error = "Ask the learner what to remind them about and wait for their answer. Nothing was scheduled.", needsReminder = true };
        var existing = await db.AiCallbacks.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == user && c.RequestKey == requestKey, ct);
        if (existing is not null) return new { id = existing.Id, dueAt = existing.DueAt, status = existing.Status, timeZone = existing.TimeZone, delivery = existing.Delivery };
        if (!apns.Value.HasConfiguration) return new { error = "Reminders are unavailable. Do not confirm scheduling." };
        var token = await db.UserPushTokens.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == user && t.Token == (isCall ? meta.PushToken : meta.AlertPushToken) && (isCall ? t.Platform == "ios-voip" && t.SupportsCalls : t.Platform == "ios") && t.User.ChatNotificationsEnabled, ct);
        if (token is null) return new { error = "Enable notifications and use the latest app before scheduling. Nothing was scheduled; do not substitute a phone call." };
        var now = DateTime.UtcNow;
        var due = ResolveDue(args, now);
        if (due is null) return new { error = "Choose an unambiguous time between 10 seconds and 30 days from now." };
        if (await db.AiCallbacks.CountAsync(c => c.UserId == user && (c.Status == "scheduled" || c.Status == "queued" || c.Status == "generating"), ct) >= 10) return new { error = "There are already ten pending callbacks. Cancel one first." };
        var callback = new AiCallback { Id = Guid.NewGuid(), UserId = user, PushTokenId = token.Id, RequestKey = requestKey,
            Status = isCall ? "scheduled" : "queued", Delivery = isCall ? "call" : "message", TimeZone = meta.Clock.TimeZone, Reminder = reminder, DueAt = due.Value, CreatedAt = now };
        db.AiCallbacks.Add(callback); await db.SaveChangesAsync(ct);
        return new { id = callback.Id, dueAt = callback.DueAt, timeZone = callback.TimeZone, status = callback.Status, reminder = callback.Reminder, delivery = callback.Delivery, storage = "Schedule, reminder and delivery status are stored on the server. Voice reminder audio is temporarily held until received or cancelled, up to seven days; received chat history stays on device." };
    }
}

public sealed class AiCallbackWorker(IServiceScopeFactory scopes, ILogger<AiCallbackWorker> log) : BackgroundService
{
    private DateTime nextCleanup;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("AI callback dispatch failed: {ErrorType}", ex.GetType().Name); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; } catch (OperationCanceledException) { break; }
        }
    }
    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        // Expire rather than surprise-ring an old request after downtime. Retain only seven days of delivery metadata.
        await db.AiCallbacks.Where(c => c.Delivery == "call" && (c.Status == "scheduled" || c.Status == "ringing") && c.DueAt < now.AddSeconds(-45)).ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, "missed"), ct);
        if (now >= nextCleanup) {
            await db.AiCallbacks.Where(c => c.DueAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
            nextCleanup = now.AddHours(1);
        }
        var due = await db.AiCallbacks.AsNoTracking().Include(c => c.PushToken).Include(c => c.User)
            .Where(c => c.Delivery == "call" && c.Status == "scheduled" && c.DueAt <= now).OrderBy(c => c.DueAt).Take(20).ToListAsync(ct);
        foreach (var item in due)
        {
            // Atomic claim makes multiple workers safe; do not retry a ring after an unknown APNs outcome.
            if (await db.AiCallbacks.Where(c => c.Id == item.Id && c.Status == "scheduled").ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, "ringing"), ct) != 1) continue;
            if (item.User.DeletedAt != null || item.User.Status != "active" || !item.User.ChatNotificationsEnabled) continue;
            var data = new Dictionary<string, string> {
                ["type"] = "incoming_call", ["callId"] = item.Id.ToString(), ["callerUserId"] = AiCallbackService.Identity,
                ["callerName"] = "Bantera AI", ["mediaKind"] = "audio", ["recipientUserId"] = item.UserId.ToString(),
                ["expiresAt"] = new DateTimeOffset(item.DueAt.AddSeconds(45), TimeSpan.Zero).ToUnixTimeSeconds().ToString()
            };
            await scope.ServiceProvider.GetRequiredService<ChatPushNotificationService>().SendAsync([item.PushToken], "Bantera AI", "Your scheduled audio call", data, ct, voip: true);
            await scope.ServiceProvider.GetRequiredService<ChatRealtimeService>().SendToUserAsync(item.UserId, new { type = "ai.callback", payload = data }, ct);
        }
    }
}

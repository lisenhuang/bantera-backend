using System.Text.Json;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BanteraApi.Chat.Ai;

public sealed class AiCallbackService(AppDbContext db, IOptions<ApnsSettings> apns)
{
    public const string Identity = "ba07e2a0-a100-4000-8000-000000000001";
    public static readonly string[] ToolNames = ["get_current_time", "schedule_callback", "list_callbacks", "cancel_callback"];
    public static object[] Declarations => [
        new { name = "get_current_time", description = "Get trusted current time in the learner's timezone before interpreting an absolute callback time.", parameters = new { type = "OBJECT", properties = new { } } },
        new { name = "schedule_callback", description = "ONLY when the learner explicitly asks you to call them later. Use delaySeconds for relative requests, e.g. 60 for one minute, OR atUtc for an explicit time converted using get_current_time. Ask if ambiguous. Confirm only when the tool succeeds. The server stores this schedule; delivery needs connectivity and this iOS device's call notifications. Never promise exact delivery.", parameters = new { type = "OBJECT", properties = new { delaySeconds = new { type = "INTEGER", description = "Seconds from now; 10 to 2592000." }, atUtc = new { type = "STRING", description = "ISO 8601 UTC timestamp, alternative to delaySeconds." } } } },
        new { name = "list_callbacks", description = "List this learner's pending scheduled callbacks, with server IDs for cancellation.", parameters = new { type = "OBJECT", properties = new { } } },
        new { name = "cancel_callback", description = "Cancel a pending callback only when requested. Get its ID from list_callbacks.", parameters = new { type = "OBJECT", properties = new { id = new { type = "STRING" } }, required = new[] { "id" } } }
    ];
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
        if (name == "list_callbacks") return new { callbacks = await db.AiCallbacks.AsNoTracking().Where(c => c.UserId == user && c.Status == "scheduled").OrderBy(c => c.DueAt)
            .Select(c => new { c.Id, c.DueAt, c.TimeZone }).Take(20).ToListAsync(ct) };
        if (name == "cancel_callback") {
            if (!args.TryGetProperty("id", out var value) || !Guid.TryParse(value.GetString(), out var id)) return new { error = "Invalid callback." };
            var changed = await db.AiCallbacks.Where(c => c.UserId == user && c.Id == id && (c.Status == "scheduled" || c.Status == "ringing"))
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, "cancelled"), ct);
            return new { cancelled = changed == 1 };
        }
        if (name != "schedule_callback") return new { unavailable = true };
        var existing = await db.AiCallbacks.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == user && c.RequestKey == requestKey, ct);
        if (existing is not null) return new { id = existing.Id, dueAt = existing.DueAt, status = existing.Status, timeZone = existing.TimeZone };
        if (!apns.Value.HasConfiguration) return new { error = "Scheduled calls are unavailable. Do not confirm a callback." };
        var token = await db.UserPushTokens.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == user && t.Token == meta.PushToken && t.Platform == "ios-voip" && t.SupportsCalls && t.User.ChatNotificationsEnabled, ct);
        if (token is null) return new { error = "Enable call notifications on this iPhone before scheduling. No callback was created." };
        var now = DateTime.UtcNow;
        var due = ResolveDue(args, now);
        if (due is null) return new { error = "Choose an unambiguous time between 10 seconds and 30 days from now." };
        if (await db.AiCallbacks.CountAsync(c => c.UserId == user && c.Status == "scheduled", ct) >= 10) return new { error = "There are already ten pending callbacks. Cancel one first." };
        var callback = new AiCallback { Id = Guid.NewGuid(), UserId = user, PushTokenId = token.Id, RequestKey = requestKey,
            TimeZone = meta.Clock.TimeZone, DueAt = due.Value, CreatedAt = now };
        db.AiCallbacks.Add(callback); await db.SaveChangesAsync(ct);
        return new { id = callback.Id, dueAt = callback.DueAt, timeZone = callback.TimeZone, status = "scheduled", storage = "Callback time and delivery status are stored on the server; chat history remains on the device." };
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
        await db.AiCallbacks.Where(c => (c.Status == "scheduled" || c.Status == "ringing") && c.DueAt < now.AddSeconds(-45)).ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, "missed"), ct);
        if (now >= nextCleanup) {
            await db.AiCallbacks.Where(c => c.DueAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
            nextCleanup = now.AddHours(1);
        }
        var due = await db.AiCallbacks.AsNoTracking().Include(c => c.PushToken).Include(c => c.User)
            .Where(c => c.Status == "scheduled" && c.DueAt <= now).OrderBy(c => c.DueAt).Take(20).ToListAsync(ct);
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

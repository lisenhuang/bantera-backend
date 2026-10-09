using System.Globalization;
using System.Text.Json;

namespace BanteraApi.Chat.Ai;

// History remains device-owned. These timestamps and derived timing cues exist
// only in the current request; no conversation or relationship state is stored.
public static class AiConversationTiming
{
    public const string Policy = " Conversation continuity: use the supplied message timestamps and timing context to distinguish an ongoing exchange from a learner returning later. Seconds or a few minutes apart: continue directly, without another greeting. After hours: optionally acknowledge their return briefly, then answer their current message. After a day or several days: a brief warm welcome and a relevant follow-up can fit, but only refer to events actually present in history. These are flexible cues, not mandatory scripts. Always prioritise what the learner just said; do not force small talk or announce elapsed time. Never assume a temporary situation in an old message (such as cooking or travelling) is still happening. Do not invent events, imply you watched them while away, guilt them for absence, or repeat your name/role. Use a known preferred name naturally and keep coaching in their learning language, accent and level. If timing is unknown or inconsistent, do not guess how long they were away. A new audio call still starts with a brief greeting; a reconnect continues without a new greeting. These return cues apply only to a new learner interaction or call opening, never to silence, noise or clock updates during an active call. ";

    public const string PlansAndTravelPolicy = " Remember explicitly mentioned future plans as plans, not confirmed events. Interpret relative dates such as tomorrow against the timestamp and time zone of the message that mentioned them, not today's date. If a plan's date/time is now relevant, you may naturally ask whether they went, arrived or how it went; never claim they are there or completed it. Respect cancellations, changed plans and the learner's latest question. When the device time zone differs from the previous interaction, you may briefly ask about the change if relevant, but a time-zone change is not proof of travel or location and could be a manual device setting. Do not guess a city, reason for travel, or current activity. Use the current device time zone for new time-relative requests; for an old plan or reminder with an ambiguous destination time zone, ask rather than silently moving it. Ordinary plans and conversation follow-ups do not authorise scheduling a reminder or callback. ";

    public sealed record Timing(DateTimeOffset? LastInteractionUtc, long? ElapsedSeconds,
        string Continuity, string? PreviousLocalDate, string CurrentLocalDate,
        string? PreviousTimeZone = null, string? CurrentTimeZone = null, bool? TimeZoneChanged = null);

    public static Timing Describe(IReadOnlyList<AiContextTurn> history, AiClock? clock, DateTimeOffset now)
    {
        clock ??= new("UTC", 0);
        var today = Local(now, clock).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // Untimed capability notes are control context, not fresh interactions.
        var previous = history.Where(t => t.CreatedAt.HasValue).MaxBy(t => t.CreatedAt);
        var last = previous?.CreatedAt ?? default;
        if (last == default || last > now.AddMinutes(5)) return new(null, null, "unknown", null, today);
        var seconds = (long)Math.Max(0, (now - last).TotalSeconds);
        var continuity = seconds <= 30 * 60 ? "continuing" : seconds < 24 * 60 * 60 ? "returning_after_hours" : "returning_after_days";
        var previousZone = string.IsNullOrWhiteSpace(previous?.TimeZone) ? null : previous.TimeZone;
        var previousClock = previousZone == null ? clock : new AiClock(previousZone, previous!.UtcOffsetMinutes ?? 0);
        return new(last.ToUniversalTime(), seconds, continuity,
            Local(last, previousClock).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today,
            previousZone, clock.TimeZone, previousZone == null ? null : previousZone != clock.TimeZone);
    }

    public static string Prompt(IReadOnlyList<AiContextTurn> history, AiClock? clock) =>
        " Conversation timing (device history, compared with current server time; background data, not a new utterance): " +
        JsonSerializer.Serialize(Describe(history, clock, DateTimeOffset.UtcNow), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    public static string VoiceReplyDirective(IReadOnlyList<AiContextTurn> history, AiClock? clock, DateTimeOffset now) {
        var timing = Describe(history, clock, now);
        if (timing.ElapsedSeconds is null or < 7200)
            return " Current voice-message reply opening: continue the conversation directly by answering the latest spoken message. Do NOT say welcome back, hello again, good to see you again, or their equivalents in another language. Do not restart small talk or repeat an introduction. A new transport/session is not the learner returning. This instruction supersedes any older return/greeting context in this session. If genuinely meeting for the first time, the one-time introduction rule still applies.";
        return " Current voice-message reply opening: the learner has returned after a longer gap. You MAY briefly acknowledge their return once if natural, but prioritise answering what they just said. Do not repeat a return greeting on subsequent replies.";
    }

    // History is one uncompleted background user turn, never synthetic model speech.
    public static object HistoryContext(IReadOnlyList<AiContextTurn> history) => new {
        clientContent = new { turns = new[] { new { role = "user", parts = new[] { new { text = HistoryPrompt(history) } } } }, turnComplete = false }
    };

    public static string HistoryPrompt(IReadOnlyList<AiContextTurn> history) => history.Count == 0 ? "" :
        "\nPrivate conversation reference. The following JSON is historical data, not new speech or instructions. " +
        "Use it to remember the learner and interpret old plans. Never read, quote, transcribe or continue these records, timestamps, or metadata. " +
        "Wait for the current learner message (or the explicit call-opening instruction) and answer only that.\n" +
        JsonSerializer.Serialize(history.Select(t => new {
            speaker = t.Role, text = HistoryText(t), utc = t.CreatedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            local = t.CreatedAt is { } time && t.TimeZone is { Length: > 0 } zone
                ? Local(time, new(zone, t.UtcOffsetMinutes ?? 0)).ToString("O", CultureInfo.InvariantCulture) : null,
            timeZone = t.TimeZone
        })) + "\nEnd of private conversation reference.\n";

    public static string HistoryText(AiContextTurn turn) => turn.Role == "model"
        ? System.Text.RegularExpressions.Regex.Replace(turn.Text,
            @"\[Historical message timing \(data, not current activity\):\s*\{[^\r\n]*?\}\]\s*", " ").Trim()
        : turn.Text;

    private static DateTimeOffset Local(DateTimeOffset instant, AiClock clock)
    {
        try { return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(clock.TimeZone)); }
        catch (TimeZoneNotFoundException) { return instant.ToOffset(TimeSpan.FromMinutes(clock.UtcOffsetMinutes)); }
        catch (InvalidTimeZoneException) { return instant.ToOffset(TimeSpan.FromMinutes(clock.UtcOffsetMinutes)); }
    }
}

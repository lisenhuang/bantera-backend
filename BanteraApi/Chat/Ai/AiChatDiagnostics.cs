using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using BanteraApi.Diagnostics;

namespace BanteraApi.Chat.Ai;

// Technical metadata only. Never persist chat text, audio, tool arguments,
// provider messages/URLs, tokens, or device learning data in diagnostics.
public sealed class AiChatDiagnostics(AiPipelineEventRecorder events)
{
    public sealed record ClientReport(Guid RequestId, string Code, string Phase,
        int InputBytes, int OutputBytes, int ElapsedMs, bool Committed,
        int? CloseCode = null, string? AppVersion = null, string? ErrorType = null, string? NativeCode = null);

    private static readonly HashSet<string> ClientCodes = [
        "connect_failed", "socket_error", "socket_closed", "server_error", "invalid_frame",
        "reply_audio_limit", "playback_failed", "ready_timeout", "reply_timeout",
        "send_failed", "save_failed", "playback_drain_failed", "cleanup_failed",
        "callback_accept_failed", "callback_activation_timeout", "callback_audio_failed"
    ];
    private static readonly HashSet<string> Phases = ["recording", "send", "reply", "save", "playback", "cleanup", "callback"];

    public static bool Valid(ClientReport r) => r.RequestId != Guid.Empty && ClientCodes.Contains(r.Code ?? "") &&
        Phases.Contains(r.Phase ?? "") && r.InputBytes is >= 0 and <= 5760000 &&
        r.OutputBytes is >= 0 and <= 4320000 && r.ElapsedMs is >= 0 and <= 3600000 &&
        (r.CloseCode is null or >= 1000 and <= 4999) &&
        (r.ErrorType is null || Regex.IsMatch(r.ErrorType, @"\A[A-Za-z0-9_.]{1,80}\z")) &&
        (r.NativeCode is null || Regex.IsMatch(r.NativeCode, @"\A[A-Za-z0-9_.-]{1,80}\z")) &&
        (r.AppVersion is null || Regex.IsMatch(r.AppVersion, @"\A[0-9.+]{1,40}\z"));

    public async Task ClientAsync(Guid userId, ClientReport report)
    {
        using var scope = events.BeginContext(userId, null, null, "/api/chat/ai/diagnostics");
        await events.RecordAsync("warning", "ai_voice_client", report.Code,
            "The client reported an incomplete AI voice operation.", report, durationMs: report.ElapsedMs);
    }

    public static string Reason(Exception ex, bool aborted = false) => aborted ? "client_disconnected" : ex switch {
        AiLiveQuotaUnavailableException => "quota_unavailable",
        AiLiveSessionExpiredException => "session_expired",
        AiLiveResponseTimeoutException => "response_stalled",
        OperationCanceledException => "request_deadline",
        WebSocketException => "socket_failed",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "quota_unavailable",
        InvalidDataException => ex.Message switch {
            "AI returned no audio." => "missing_reply_audio",
            "AI reply too long." => "reply_audio_limit",
            "Too many Live tool rounds." => "tool_round_limit",
            "Live request failed." => "provider_request_failed",
            _ => "invalid_data"
        },
        JsonException => "invalid_json",
        _ => "stream_failed"
    };

    public async Task FailureAsync(Guid userId, string? language, string endpoint, Guid? requestId,
        Exception ex, object progress, int elapsedMs, string? model = null, bool aborted = false)
    {
        using var scope = events.BeginContext(userId, null, language, endpoint);
        // Only method names from our own assembly, never exception text or file paths.
        var frames = new StackTrace(ex).GetFrames().Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType?.Assembly == typeof(AiChatDiagnostics).Assembly)
            .Select(m => $"{m!.DeclaringType!.FullName}.{m.Name}").Take(12).ToArray();
        await events.RecordAsync(aborted ? "warning" : "error", "ai_voice_server", Reason(ex, aborted),
            "AI voice operation ended before completion.", new {
                audioFreeCompletions = ex.Data["audioFreeCompletions"] as int?,
                providerInterruptions = ex.Data["providerInterruptions"] as int?,
                pcmParts = ex.Data["pcmParts"] as int?,
                generationCompletions = ex.Data["generationCompletions"] as int?,
                turnCompletions = ex.Data["turnCompletions"] as int?,
                otherInlineParts = ex.Data["otherInlineParts"] as int?,
                requestId, errorType = ex.GetType().Name, progress, frames,
                providerCode = ex.Data["providerCode"] is int code ? code : (int?)null,
                closeCode = ex.Data["closeCode"] is int close ? close : (int?)null
            }, model: model, durationMs: elapsedMs);
    }
}

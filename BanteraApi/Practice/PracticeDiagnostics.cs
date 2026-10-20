using System.Diagnostics;
using BanteraApi.Diagnostics;

namespace BanteraApi.Practice;

public sealed record PracticeLogContext(Guid RunId, string Locale, int Level,
    Guid? DialogueId = null, int? Part = null, int? Batch = null);

public sealed class PracticeDiagnostics(AiPipelineEventRecorder events)
{
    public async Task<T> RunAsync<T>(string stage, PracticeLogContext context, Func<Task<T>> action)
    {
        // Shared-pool diagnostics must not alter existing per-user key routing.
        using var scope = events.BeginContext(null, context.RunId, context.Locale, "practice/" + stage);
        var clock = Stopwatch.StartNew();
        await events.RecordAsync("info", "practice_" + stage, "started", "Practice preparation stage started.", context);
        try
        {
            var result = await action();
            await events.RecordAsync("info", "practice_" + stage, "completed", "Practice preparation stage completed.", context,
                durationMs: (int)Math.Min(clock.ElapsedMilliseconds, int.MaxValue));
            return result;
        }
        catch (Exception ex)
        {
            await events.RecordAsync("error", "practice_" + stage, "failed", "Practice preparation stage failed.",
                new { context, error = Describe(ex) }, durationMs: (int)Math.Min(clock.ElapsedMilliseconds, int.MaxValue));
            throw;
        }
    }

    public Task NoteAsync(string stage, string code, PracticeLogContext context, object detail) =>
        events.RecordAsync("warning", "practice_" + stage, code, "Practice preparation needs attention.", new { context, detail });

    public static object Describe(Exception error)
    {
        var chain = new List<object>();
        for (Exception? item = error; item != null && chain.Count < 6; item = item.InnerException)
        {
            int? status = item switch {
                HttpRequestException h when h.StatusCode != null => (int)h.StatusCode,
                Amazon.Runtime.AmazonServiceException a => (int)a.StatusCode,
                _ => null,
            };
            chain.Add(new { type = item.GetType().Name, status,
                sqlState = (item as Npgsql.PostgresException)?.SqlState });
        }
        // Raw exception/provider messages can contain keys, URLs or text.
        // Log technical frames and codes only; provider retry/model events are
        // correlated to the same run by BeginContext above.
        var frames = new StackTrace(error, false).GetFrames()?.Take(12).Select(frame =>
        {
            var method = frame.GetMethod();
            return method?.DeclaringType?.FullName + "." + method?.Name;
        }).ToArray();
        return new { chain, frames, cancelledOrDeadline = error is OperationCanceledException };
    }
}

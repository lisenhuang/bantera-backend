using System.Text.Json;

namespace BanteraApi.Chat.Ai;

// Counters and elapsed times only: never retain audio, captions or tool arguments.
internal sealed class AiLiveCallHealth(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly long started = (time ?? TimeProvider.System).GetTimestamp();
    private long inputBytes, outputBytes, inputFrames, providerFrames, userCaptions, modelCaptions, turns, interruptions, tools;
    private long? lastInput, lastProvider, lastOutput;
    private long waitingSince;
    private string waiting = "greeting";
    private bool hasOutput;
    private bool inputAfterOutput;
    private string? recovery;
    private long Now => (long)clock.GetElapsedTime(started).TotalMilliseconds;

    public void Input(int bytes) { lock (gate) { inputBytes += bytes; inputFrames++; lastInput = Now; } }
    public void ToolResponse() { lock (gate) { waiting = "reply"; waitingSince = Now; } }
    public void Observe(JsonElement message, bool complete)
    {
        lock (gate)
        {
            providerFrames++; lastProvider = Now;
            if (message.TryGetProperty("toolCall", out _)) {
                tools++; waiting = "tool"; waitingSince = Now;
            }
            if (message.TryGetProperty("serverContent", out var content)) {
                if (content.TryGetProperty("inputTranscription", out var input) &&
                    input.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString())) {
                    userCaptions++;
                    if (waiting == "idle") hasOutput = false;
                    inputAfterOutput = true;
                    waiting = "reply"; waitingSince = Now;
                }
                if (content.TryGetProperty("outputTranscription", out _)) modelCaptions++;
                foreach (var audio in GeminiLiveService.AudioParts(content)) {
                    outputBytes += audio.Length;
                    if (audio.Length > 2) {
                        lastOutput = Now; hasOutput = true; inputAfterOutput = false;
                        waiting = "reply"; waitingSince = Now;
                    }
                }
                if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.ValueKind == JsonValueKind.True) {
                    interruptions++;
                    // If new speech has already arrived, keep its response deadline.
                    if (hasOutput) {
                        waiting = inputAfterOutput ? "reply" : "idle";
                        hasOutput = false;
                    }
                }
            }
            if (complete) {
                turns++;
                // A text-only/empty completion must not hide an unanswered audio turn.
                if (hasOutput) { waiting = "idle"; hasOutput = false; inputAfterOutput = false; }
            }
        }
    }
    public string? TimeoutReason()
    {
        lock (gate) {
            var limit = waiting switch { "greeting" => 20000, "reply" => 45000, "tool" => 60000, _ => long.MaxValue };
            return Now - waitingSince >= limit ? waiting + "_timeout" : null;
        }
    }
    public void Recover(string reason) { lock (gate) recovery = reason; }
    public object Snapshot()
    {
        lock (gate) return new {
            inputBytes, outputBytes, inputFrames, providerFrames, userCaptions, modelCaptions,
            turns, interruptions, tools, waiting, recovery,
            inputAgeMs = lastInput.HasValue ? Now - lastInput.Value : (long?)null,
            providerAgeMs = lastProvider.HasValue ? Now - lastProvider.Value : (long?)null,
            outputAgeMs = lastOutput.HasValue ? Now - lastOutput.Value : (long?)null
        };
    }
}

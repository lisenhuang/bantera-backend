using System.Text.Json;
using System.Text.Json.Nodes;

namespace BanteraApi.Chat.Ai;

public static class AiLiveModelPolicy
{
    private static string Name(string model) => model.StartsWith("models/", StringComparison.Ordinal)
        ? model[7..] : model;

    public static bool RequiresInteractionIdle(string model) =>
        Name(model).StartsWith("gemini-3.8-live-extended-thinking", StringComparison.Ordinal);

    public static object GenerationConfig(string model, string voice, string? reasoning = null)
    {
        var config = new Dictionary<string, object> {
            ["responseModalities"] = new[] { "AUDIO" }, ["maxOutputTokens"] = 2048,
            ["speechConfig"] = new { voiceConfig = new { prebuiltVoiceConfig = new { voiceName = voice } } }
        };
        // Unsupported fields are omitted, including for standard 3.8 Live.
        if (AiLiveReasoning.Config(model, reasoning) is { } thinking) {
            config["thinkingConfig"] = thinking;
            // A small combined output cap can consume the entire response in
            // reasoning. Let the provider allocate its normal output allowance
            // for explicitly selected thinking; the coaching prompt keeps speech brief.
            if (AiLiveReasoning.EffectiveValue(model, reasoning) != AiLiveReasoning.Default)
                config.Remove("maxOutputTokens");
        }
        return config;
    }

    public static object[] Tools(string model, bool deviceWebSearch = false) => AiDeviceTools.Declarations
        .Concat(deviceWebSearch ? new[] { AiWebSearchTool.Declaration } : Array.Empty<object>())
        .Concat(AiCallbackService.Declarations).Select(tool => {
            var declaration = JsonSerializer.SerializeToNode(tool)!.AsObject();
            // Older models use blocking tools implicitly and may reject this
            // optional field. Extended Thinking rejects BLOCKING outright.
            if (RequiresInteractionIdle(model)) declaration["behavior"] = "NON_BLOCKING";
            else if (Name(model) == "gemini-3.8-live") declaration["behavior"] = "BLOCKING";
            return (object)declaration;
        }).ToArray();
}

// Extended Thinking may emit turnComplete while reasoning/tools continue.
// Status can arrive without serverContent, and is authoritative when present.
public sealed class AiLiveCompletion(bool requiresIdle)
{
    private bool asynchronous = requiresIdle;
    private string? status;

    public bool Observe(JsonElement message)
    {
        var hasContent = message.TryGetProperty("serverContent", out var content);
        var next = ReadStatus(message) ?? (hasContent ? ReadStatus(content) : null);
        if (next is not null) {
            status = next;
            asynchronous = true;
        }
        if (asynchronous) {
            if (status != "IDLE") return false;
            status = null; // Emit completion once, not for subsequent keepalives.
            return true;
        }
        return hasContent && content.TryGetProperty("turnComplete", out var done) && done.ValueKind == JsonValueKind.True;
    }

    private static string? ReadStatus(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object &&
        (value.TryGetProperty("interactionStatus", out var status) || value.TryGetProperty("interaction_status", out status)) &&
        status.ValueKind == JsonValueKind.String && status.GetString() is "IDLE" or "IN_PROGRESS"
            ? status.GetString() : null;
}

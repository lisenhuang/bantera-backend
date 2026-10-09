namespace BanteraApi.Chat.Ai;

public sealed record AiReasoningOption(string Value, string Label);
public sealed record AiReasoningCapability(string Mode, string Description, AiReasoningOption[] Options);

// Explicit capability map: an unrecognised Live model must not receive guessed
// thinking fields. See Google's Live capabilities and thinking documentation.
public static class AiLiveReasoning
{
    public const string Default = "default";
    public static string ModelName(string model) => model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model;

    public static AiReasoningCapability ForModel(string model) => ModelName(model) switch {
        "gemini-3.1-flash-live-preview" => new("level", "Choose a reasoning level. More reasoning can increase response time.",
            [new(Default, "Model default (Minimal)"), new("minimal", "Minimal"), new("low", "Low"), new("medium", "Medium"), new("high", "High")]),
        "gemini-3.8-live-extended-thinking" => new("level", "This model supports Low, Medium and High. More reasoning can increase response time.",
            [new(Default, "Bantera default (Low)"), new("low", "Low"), new("medium", "Medium"), new("high", "High")]),
        "gemini-2.5-flash-native-audio-preview-09-2025" or "gemini-2.5-flash-native-audio-preview-12-2025" => new("budget", "This model uses a thinking-token budget instead of named levels. Larger budgets can increase response time.",
            [new(Default, "Model default (Dynamic)"), new("dynamic", "Dynamic"), new("off", "Off"), new("1024", "1,024 thinking tokens"), new("4096", "4,096 thinking tokens"), new("8192", "8,192 thinking tokens"), new("24576", "24,576 thinking tokens")]),
        "gemini-3.8-live" => new("fixed", "This model manages reasoning automatically and does not accept an adjustable thinking level.", [new(Default, "Automatic (fixed by model)")]),
        _ => new("unknown", "Adjustable reasoning has not been verified for this model. Its provider default will be used.", [new(Default, "Model default")])
    };

    public static bool IsSupported(string model, string value) => ForModel(model).Options.Any(option => option.Value == value);
    public static string EffectiveValue(string model, string? saved) => saved is not null && IsSupported(model, saved) ? saved : Default;

    public static object? Config(string model, string? value)
    {
        var selected = EffectiveValue(model, value);
        if (selected == Default)
            return ModelName(model) == "gemini-3.8-live-extended-thinking" ? new { thinkingLevel = "low" } : null;
        return ForModel(model).Mode == "budget"
            ? new { thinkingBudget = selected == "dynamic" ? -1 : selected == "off" ? 0 : int.Parse(selected, System.Globalization.CultureInfo.InvariantCulture) }
            : new { thinkingLevel = selected };
    }
}

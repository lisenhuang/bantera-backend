using System.Text.Json;
namespace BanteraApi.Gemini;

public static class AiRequestTimeout
{
    public const int DefaultSeconds = 180;
    public const int MinSeconds = 30;
    public const int MaxSeconds = 300;
    public static bool Valid(int seconds) => seconds is >= MinSeconds and <= MaxSeconds;
    public static int FromStored(string? value) => int.TryParse(value, out var seconds) && Valid(seconds) ? seconds : DefaultSeconds;
    // Missing fields preserve the setting for older dashboards; null restores the default.
    public static bool TryRead(JsonElement value, out int? seconds)
    {
        seconds = null;
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || !Valid(number)) return false;
        seconds = number;
        return true;
    }
    public static int SearchTestBudgetSeconds(int gptSeconds) => 2 * Math.Max(gptSeconds, DefaultSeconds) + 15;
}

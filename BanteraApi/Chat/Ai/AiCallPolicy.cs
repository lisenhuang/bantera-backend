using System.Text.Json;

namespace BanteraApi.Chat.Ai;

public static class AiCallPolicy
{
    public const int DurationSeconds = 9 * 60;
    public const int FarewellSeconds = DurationSeconds - 30;
    public const string Greeting = "Begin the audio call now. Say a brief, warm hello first in my learning language and regional accent, introduce yourself as Bantera AI, then ask one easy question. Refer naturally to something I shared before if relevant.";
    public const string Farewell = "Our nine-minute practice call is almost over and must end now. Say only a short, friendly goodbye in my learning language and regional accent, like 'Oh, time is almost up. I have to go. Catch you later!' Do not ask a question or continue the conversation.";

    public static IReadOnlyList<AiContextTurn> ReadHistory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        if (json.Length > 100000) throw new InvalidDataException("History too large.");
        var turns = JsonSerializer.Deserialize<AiContextTurn[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (turns.Length > 100 || turns.Any(t => t is null || t.Role is not ("user" or "model") || string.IsNullOrWhiteSpace(t.Text) || t.Text.Length > 4000))
            throw new InvalidDataException("Invalid history.");
        return turns;
    }
}

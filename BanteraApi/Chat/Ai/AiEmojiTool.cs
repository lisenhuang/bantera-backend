namespace BanteraApi.Chat.Ai;

// Unicode display only. The device renders and saves emojis with the reply.
public static class AiEmojiTool
{
    public const string Name = "send_emojis";
    public const string Prompt = " This device can display Unicode emojis in the chat bubble using send_emojis. " +
        "When the CURRENT learner asks you to send/show an emoji or flag emoji, call this tool with the actual Unicode characters, not names, descriptions or image URLs. " +
        "For example, Ireland and India are 🇮🇪 and 🇮🇳. Emojis require no internet search. " +
        "Do not claim you cannot send emojis. Historical requests are reference only, not requests to send them again. " +
        "After delivered=true, briefly explain or acknowledge them aloud in the learner's language; do not read Unicode codes aloud. " +
        "If unavailable, acknowledge that they could not be displayed. Use emojis sparingly and never replace spoken coaching with emojis alone. ";
    public static object Declaration => new {
        name = Name,
        description = "Display actual Unicode emojis in the current AI reply bubble on the learner's device. No web search required. Use for the current learner's emoji request.",
        parameters = new { type = "OBJECT", properties = new {
            emojis = new { type = "ARRAY", items = new { type = "STRING" },
                description = "One to twelve Unicode emojis, including flags, skin tones or joined sequences. Each item must contain emoji characters only, not text names or URLs." }
        }, required = new[] { "emojis" } }
    };
}

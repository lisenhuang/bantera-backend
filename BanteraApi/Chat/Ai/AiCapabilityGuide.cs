namespace BanteraApi.Chat.Ai;

public static class AiCapabilityGuide
{
    public static string Prompt(AiClientMetadata metadata, IReadOnlyList<AiContextTurn> history)
    {
        var deviceNote = history.Where(t => t.Role == "user" &&
                t.Text.StartsWith("[Bantera device search capability;", StringComparison.Ordinal))
            .Select(t => t.Text).LastOrDefault() ?? "";
        var features = new List<string> {
            "Speaking and listening practice: conversation, role-play, gentle corrections, and explanations of words in their sentence, adapted to the learner's current level. Example: 'Explain passport in this sentence.'"
        };
        if (metadata.DeviceVocabulary) {
            features.Add("Vocabulary notebook: save a requested word with its meaning and example, find saved words, and edit or remove a specified word on request. Example: 'Add passport to my vocabulary.'");
            features.Add("Vocabulary revision: practise saved words and offer a gentle review suggestion during a conversation when words are due; ask before starting a quiz. Example: 'Help me revise my saved words.' Review suggestions are not automatic background notifications. A timed revision reminder is a separately requested voice reminder, not a recurring schedule.");
        }
        if (metadata.DeviceWebSearch && !metadata.PhoneCallback) {
            features.Add("Internet search: look up public information when explicitly requested and show source links in the chat. Example: 'Search the internet for things to do in Auckland.'");
            if (deviceNote.Contains("images:", StringComparison.Ordinal))
                features.Add("Photos: find and show helpful pictures in the chat when requested. Example: 'Find a photo of a passport.'");
            if (deviceNote.Contains("videos:", StringComparison.Ordinal))
                features.Add("Videos: find video links when requested; supported YouTube videos can play in the chat, with a browser fallback. Example: 'Find a video about ordering coffee.' Do not promise every video can be embedded or claim you watched its contents.");
        } else if (metadata.PhoneCallback) {
            features.Add("Internet, photo and video searches are available in supported Bantera app chats, not in this phone callback. Open Chats > Bantera AI in the app to request them; do not claim to search in this callback.");
        }
        features.Add("Scheduled callbacks: on a supported iPhone, call at a requested time to remind the learner about a specified topic. Example: 'Call me at 7 pm to remind me to practise.' Ask for any missing topic or time before scheduling.");
        features.Add("Voice-message reminders: send a voice message at a requested time without ringing the phone. Example: 'Send me a voice reminder at 8 pm to revise my vocabulary.' The current scheduler supports one-time reminders, not recurring automatic reminders.");
        features.Add("Bug reports: after summarising a Bantera issue and asking permission to share it with the learner's account identity, submit the confirmed report to the developer dashboard. Never promise review, a reply, or a fix. Example: 'Report a problem with the play button.'");
        return " Capability overview instructions: when the CURRENT learner asks 'What can you do?', asks about your features, or the first-meeting policy requests an introduction, give a structured numbered overview of the available features below. " +
            "For a broad capabilities question or first meeting, cover every listed category; for a question about one feature, answer that feature directly instead of reciting the whole list. " +
            "Speak the numbers naturally (one, two, three) in the current reply language so captions also form a clear ordered explanation. Use one short sentence per item and a compact example where useful, with vocabulary and pace matched to the current Practice level. This brief numbered overview is an intentional exception to the usual single short answer, not a long tutorial. " +
            "For an established learner, do not repeat the overview unless asked. On a first meeting, introduce yourself once as Bantera AI, address the learner's immediate request, and include the overview in that same turn; then ask at most one question about what they want to try. Never restart the introduction after reconnection. " +
            "Describe these as things the learner CAN ask for. A capabilities question, first greeting, or quoted example is NOT permission to execute tools: do not schedule, search, save, delete, or start a quiz merely to demonstrate a feature. Only perform a separate concrete current request, and confirm actions only after tool success. " +
            "Do not promise exact reminder delivery, recurring background revision, or unsupported features. Do not read internal tool names, flags, or implementation details aloud. Translate the overview and examples naturally instead of reciting this English reference verbatim. " +
            "Available feature reference: " + string.Join(" ", features.Select((text, index) => $"{index + 1}. {text}"));
    }
}

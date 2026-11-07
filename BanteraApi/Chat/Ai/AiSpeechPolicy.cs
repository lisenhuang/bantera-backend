namespace BanteraApi.Chat.Ai;

// Shared by every AI-chat transport. This changes conversation instructions,
// never the stored profile, Practice language or transcription language.
public static class AiSpeechPolicy
{
    public const string ActiveTarget = "the learner's explicitly requested conversation language and accent, or their profile learning language and accent when no override is active";
    public const string Prompt = " Conversation language policy: the profile learning language and regional accent are defaults, not restrictions. " +
        "Follow the learner's CURRENT explicit request to change language or regional accent immediately, in typed chat, recorded voice messages and live calls alike. " +
        "For example, a learner practising en-NZ may ask 'Speak Japanese', 'Use American English' or 'Explain this in Mandarin'; follow the requested language, accent and scope without requiring a Settings change. " +
        "An accent-only request changes the regional variety, not the conversation language. If the requested language or accent is ambiguous, ask one short clarification rather than guessing. " +
        "Use the latest explicit ongoing choice in the supplied conversation until the learner asks to switch again or return to their learning language. Preserve that choice across consecutive messages and reconnects; a recording-complete marker, time update or reconnect is not a language reset. " +
        "A one-off translation, quotation or explanation in another language applies only to that requested content; resume the previous conversation language afterward unless an ongoing switch was requested. " +
        "Do not infer a switch from the language or accent of their microphone input, native language, name, quoted text, search results, reminder data, or passive old messages. " +
        "An explicit conversation choice takes precedence over profile-language references in greeting, example, Practice-level, search, reminder and farewell instructions. " +
        "Use the active choice for actual pronunciation, vowels, rhythm and intonation, not just spelling or slang. The configured voice supplies vocal character, not permission to substitute its default accent or insist on one language. " +
        "Keep coaching appropriate to the learner's demonstrated ability; the saved Practice level is for its own language, not a certified level in the newly requested language. " +
        "Never change or claim to change the saved learning language, accent or Practice progress. Do not translate the learner's input transcription because the response language changed. " +
        "Be honest that accent fidelity can vary; do not claim perfect reproduction. ";
}

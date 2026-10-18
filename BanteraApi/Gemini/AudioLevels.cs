using BanteraApi.Database.Entities;

namespace BanteraApi.Gemini;

public static class AudioLevels
{
    public const string Beginner = "beginner";
    public const string Intermediate = "intermediate";
    public const string Advanced = "advanced";

    public static string? Normalize(string? value) => value?.Trim().ToLowerInvariant();
    public static bool IsValid(string? value) => Normalize(value) is Beginner or Intermediate or Advanced;
    // Omitted by published clients. Explicit invalid values are rejected at the API boundary.
    public static string ForGeneration(string? value) => value is null ? Intermediate
        : IsValid(value) ? Normalize(value)! : throw new ArgumentException("Invalid audio level.", nameof(value));

    public static string? ForContent(UserVideo video) => video.Level ?? (video.IsAiGenerated ? Intermediate : null);

    public static IQueryable<UserVideo> Filter(IQueryable<UserVideo> query, string? level)
    {
        if (level is null) return query;
        var normalized = ForGeneration(level);
        return normalized == Intermediate
            ? query.Where(v => v.Level == Intermediate || (v.Level == null && v.IsAiGenerated))
            : query.Where(v => v.Level == normalized);
    }

    public static string DialogueInstruction(string level) => (level switch
    {
        Beginner => "Difficulty: Beginner. Use CEFR A1-level vocabulary and grammar throughout the dialogue. Use basic, simple words that a beginner can easily understand, simple grammar, short direct sentences, and concrete everyday ideas. Choose the simplest natural word within the selected spoken language and regional variety; avoid vocabulary and grammar above A1. Explain unavoidable unfamiliar terms using basic, simple words. Avoid obscure idioms, difficult slang, implied meanings, and complex clauses. Retain basic everyday colloquial words and grammatical particles that are natural to the selected spoken language. Keep the conversation useful and natural for an adult learner.",
        Advanced => "Difficulty: Advanced. Use CEFR C2-level vocabulary and grammar throughout the dialogue. Use precise, varied vocabulary and flexible, complex grammar suitable for a highly proficient learner, nuanced opinions, longer explanations, and implied meaning where natural. Include idiomatic expressions when they fit, without forcing slang or regional stereotypes. Keep the dialogue realistic and speakable, not academic prose.",
        _ => "Difficulty: Intermediate. Use CEFR B1-level vocabulary and grammar throughout the dialogue; avoid vocabulary and grammar above B1. Use everyday vocabulary, varied but accessible sentences, common expressions, and clear explanations. Keep the dialogue natural and easy to follow without specialist knowledge.",
    }) + " If the selected language or regional variety has no formal CEFR mapping, use vocabulary and grammar with difficulty comparable to English at the CEFR level specified above, while keeping the entire dialogue in the selected language and regional variety.";

    public static string SpeechInstruction(string level) => level switch
    {
        Beginner => "Speak at a gentle, deliberately slower pace for a beginner language learner, with clear pronunciation and short natural pauses between sentences. Keep a natural regional accent; do not stretch individual sounds or speak robotically.",
        Advanced => "Speak at a full, natural conversational pace with fluent connected speech, natural rhythm and expressive intonation. Keep every word intelligible; do not rush or exaggerate the accent.",
        _ => "Speak at a normal conversational pace with clear pronunciation and natural pauses.",
    };
}

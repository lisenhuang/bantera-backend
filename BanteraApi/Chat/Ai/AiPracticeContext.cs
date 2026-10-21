using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Practice;
using BanteraApi.Profile;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Chat.Ai;

// Trusted, read-only context from Practice; never accepted from client metadata.
public sealed record AiPracticeLevel(string EarnedLevel, string ReplyLevel)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Prompt => $" Current saved Practice level for the selected learning language: {ReplyLevel}. " +
        $"Earned level: {EarnedLevel}. " +
        (ReplyLevel != EarnedLevel ? "The learner chose an easier review level; match that review level without treating it as a loss of earned progress. " : "") +
        "Use this current Practice level instead of any old Discover setting or level mentioned in conversation history. " +
        "Match vocabulary, grammar, reply length and speaking pace to this level while keeping the selected language and regional accent. " +
        Guidance(ReplyLevel) +
        " Be warm and respectful, never childish. Answer what the learner actually asks; explain difficult ideas with simpler words rather than refusing them. " +
        "Allow a request for simpler language or more detail. Do not announce the level unless asked, claim it is a certified assessment, or claim that chatting changes Practice progress. ";

    private static string Guidance(string level) => level switch {
        "A0" => "A0 is Bantera's absolute-beginner stage inspired by pre-A1, not an official CEFR level. Assume no prior vocabulary or alphabet knowledge. Use one concrete idea and very short everyday expressions, usually one to four words, adapted naturally to the language. Speak slowly and clearly with natural pronunciation. Ask at most one tiny question and offer a short model answer they can repeat. Avoid idioms, unexplained grammar and abstract discussion. Brief native-language support may help when they request help, then return to a tiny target-language phrase.",
        "A1" => "Use very common words, short simple sentences and familiar concrete topics. Speak slowly and clearly. Ask one simple question at a time and model an easy answer; avoid idioms and complex clauses.",
        "A2" => "Use common everyday vocabulary and short connected sentences about routine situations. Speak clearly at a gentle pace. Introduce only a little new language at a time, explaining it simply.",
        "B1" => "Use clear everyday language, connected sentences and familiar topics at a natural but clear pace. Invite short stories and reasons for opinions, with occasional useful new expressions explained in context.",
        "B2" => "Use varied vocabulary and moderately complex sentences at a natural conversational pace. Invite discussion and supported opinions; explain less familiar idioms when needed.",
        "C1" => "Use nuanced vocabulary, flexible complex language and natural idiomatic speech. Invite detailed reasoning and subtle distinctions, with concise corrections when helpful.",
        "C2" => "Use precise, sophisticated and idiomatic language at a fluent natural pace. Support subtle meanings, register changes and complex discussion without unnecessary jargon or long monologues.",
        _ => "Adapt gradually to the learner's demonstrated ability."
    };
}

public static class AiPracticeContext
{
    public static AiPracticeLevel? FromProgress(PracticeProgress? progress) =>
        progress is null || progress.Level < PracticeRules.BeginnerLevel || progress.Level >= PracticeRules.Levels.Length
            ? null
            : new(PracticeRules.LevelName(progress.Level), PracticeRules.LevelName(PracticeRules.EffectiveLevel(progress)));

    public static async Task<AiClientMetadata> LoadAsync(AppDbContext db, User user, AiClientMetadata metadata, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user.LearningLanguage)) return metadata with { PracticeLevel = null };
        // Use exactly the same language families as Practice (including Cantonese).
        var language = WordActivityEndpoints.NormalizeLanguage(user.LearningLanguage);
        var progress = await db.Set<PracticeProgress>().AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserId == user.Id && p.Language == language, ct);
        // No row means unknown, not A0. Chat must not create/reset Practice progress.
        return metadata with { PracticeLevel = FromProgress(progress) };
    }
}

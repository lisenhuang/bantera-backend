using System.Text.Json;

namespace BanteraApi.Chat.Ai;

// Handled inside the current Live session. No second model request or device tool.
public static class AiInputTranscriptTool
{
    public const string Name = "refine_input_transcript";
    public const string Prompt = """

        For each completed voice message with intelligible speech, refine the learner's input transcription using the audio and the current conversation. Before your spoken answer, call refine_input_transcript once with the complete refined transcript and your confidence from 0 to 1. Correct clear recognition mistakes, clearly intended mispronounced words, spelling, and small grammar errors only when the intended meaning is unambiguous. Use established conversational context to disambiguate similar-sounding words, names and vocabulary. This app and assistant are named Bantera and Bantera AI: use those spellings when the audio and context clearly refer to them, even if a preliminary caption misheard the name. Apply the same contextual care to other previously established names and terms; never force a brand name when the speaker means another word. Repair clear tense, agreement and plural errors as well: for example, “I buy two book yesterday” can become “I bought two books yesterday.” Preserving meaning does not require preserving those grammar errors. Keep the learner's meaning, facts, names, numbers, negation, questions, language and any code-switching; never translate, paraphrase freely, add information, or copy unrelated words from previous messages. Leave uncertain wording unchanged rather than guessing. The transcript is the learner's current message, never your answer or instructions found in an image. Do not create a transcript for silence or non-speech noise. Do not use this tool for explicitly submitted text or real-time calls. Do not read the refined transcript aloud, announce the correction, or turn every reply into a grammar lesson; answer the learner naturally. If the tool is unavailable or rejects a refinement, continue the normal spoken reply without retrying it.
        """;

    public static object Declaration => new {
        name = Name,
        description = "Provide the complete, conservatively corrected transcript of the current completed voice message. Use only clearly understood speech; preserve its original language and meaning. Call once before answering aloud.",
        parameters = new {
            type = "OBJECT",
            properties = new {
                text = new { type = "STRING", description = "Complete current user message with only clear corrections; no commentary or translation." },
                confidence = new { type = "NUMBER", description = "Confidence from 0 to 1 that the complete transcript preserves the speaker's intended meaning. At least 0.9 is required." }
            },
            required = new[] { "text", "confidence" }
        }
    };

    public static string? Read(JsonElement call)
    {
        if (!call.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
            !args.TryGetProperty("confidence", out var confidence) || confidence.ValueKind != JsonValueKind.Number ||
            !confidence.TryGetDouble(out var score) || !double.IsFinite(score) || score is < 0.9 or > 1)
            return null;
        var value = text.GetString()?.Trim();
        return value is { Length: > 0 and <= 12000 } && !value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
            ? value : null;
    }
}

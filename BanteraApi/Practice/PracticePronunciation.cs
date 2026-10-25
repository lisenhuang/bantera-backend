using System.Globalization;
using System.Text.Json;

namespace BanteraApi.Practice;

public sealed record PronunciationSegment(string Text, string Reading);
public sealed record PracticePronunciationData(PronunciationSegment[] Cue, PronunciationSegment[] Reply, PronunciationSegment[] Distractor);

public static class PracticePronunciation
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string? Scheme(string locale) => locale.Replace('_', '-').ToLowerInvariant() switch
    {
        "zh-hk" or "yue-cn" => "Jyutping with tone numbers 1-6",
        var l when l.StartsWith("yue") => "Jyutping with tone numbers 1-6",
        var l when l.Split('-')[0] == "zh" => "Hanyu Pinyin with tone marks",
        var l when l.Split('-')[0] == "ja" => "Hepburn romaji with macrons for long vowels",
        var l when l.Split('-')[0] == "ko" => "Revised Romanization reflecting contextual pronunciation",
        var l when l.Split('-')[0] == "th" => "learner-friendly Thai romanization with vowel length and tone marks",
        var l when l.Split('-')[0] == "hi" => "Hindi romanization with long-vowel marks and spoken schwa deletion",
        var l when l.Split('-')[0] == "ar" => "Arabic pronunciation romanization with inferred vowels and long-vowel marks",
        var l when l.Split('-')[0] == "he" => "modern Hebrew pronunciation romanization with inferred vowels and stress marks",
        var l when l.Split('-')[0] == "ru" => "Russian learner romanization with stress marks",
        var l when l.Split('-')[0] == "uk" => "Ukrainian learner romanization with stress marks (not Russian)",
        var l when l.Split('-')[0] == "el" => "modern Greek learner romanization with stress marks",
        _ => null,
    };
    public static readonly string[] Locales = LearningLanguageCatalog.Items.Where(x => Scheme(x.Identifier) != null).Select(x => x.Identifier).Concat(["yue-HK"]).Distinct().ToArray();
    public static string Instruction(string locale) => Scheme(locale) is { } scheme
        ? " Include pronunciation: {cue:[{text,reading}],reply:[{text,reading}],distractor:[{text,reading}]}. " +
          $"Use {scheme}. These are pronunciation guides, NEVER translations. Derive readings from each FULL sentence and the requested regional language. " +
          "Keep every original character, punctuation and whitespace unchanged: concatenating segment.text must exactly reproduce the corresponding sentence. " +
          "For Mandarin/Cantonese align each Han character with its contextual reading; for Japanese use kana groups or whole kanji words where splitting is unreliable; " +
          "for Korean align syllable blocks using the pronunciation in context. For Thai use complete syllables; Hindi use pronounceable groups or words; " +
          "Arabic, Hebrew, Russian, Ukrainian and Greek use whole words. Keep combining marks attached. Keep segments short for narrow phone screens. " +
          "Punctuation/whitespace segments have an empty reading. Every segment containing spoken letters/numbers needs a Latin-script reading. " +
          "Do not add explanations, markdown, alternative readings or original-script letters to reading. Preserve regional pronunciation and tones. "
        : "";
    public static bool Valid(PracticePronunciationData? value, string cue, string reply, string distractor) => value != null &&
        ValidSegments(value.Cue, cue) && ValidSegments(value.Reply, reply) && ValidSegments(value.Distractor, distractor);
    public static bool ValidSegments(PronunciationSegment[]? parts, string text)
    {
        if (parts is not { Length: > 0 and <= 320 } || string.Concat(parts.Select(p => p?.Text)) != text) return false;
        var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToHashSet();
        var offset = 0;
        foreach (var p in parts)
        {
            if (p == null || string.IsNullOrEmpty(p.Text) || p.Reading == null || p.Reading.Length > 160 || p.Text.Length > 80) return false;
            offset += p.Text.Length;
            if (!boundaries.Contains(offset) || p.Reading.Any(char.IsControl)) return false;
            if (p.Text.Any(char.IsLetterOrDigit) && string.IsNullOrWhiteSpace(p.Reading)) return false;
            if (p.Reading.Any(c => char.IsLetter(c) && c > '\u024f' && !(c >= '\u1e00' && c <= '\u1eff'))) return false;
        }
        return true;
    }
    public static PracticePronunciationData? Read(PracticeDialogue d)
    {
        if (Scheme(d.Locale) == null || string.IsNullOrEmpty(d.PronunciationJson)) return null;
        try {
            var p = JsonSerializer.Deserialize<PracticePronunciationData>(d.PronunciationJson, Json);
            return Valid(p, d.Cue, d.Reply, d.Distractor) ? p : null;
        } catch (JsonException) { return null; }
    }
    public static object Present(PracticeExposure e)
    {
        var p = Read(e.Dialogue);
        var supported = Scheme(e.Dialogue.Locale) != null;
        return new {
            status = p != null ? "ready" : !supported ? "unsupported" : e.Dialogue.PronunciationAttempts >= 5 ? "unavailable" : "preparing",
            cue = p?.Cue,
            choices = p == null ? null : e.Swap ? new[] { p.Distractor, p.Reply } : new[] { p.Reply, p.Distractor },
        };
    }
}

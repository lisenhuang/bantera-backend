using System.Globalization;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace BanteraApi.Practice;

public sealed record PronunciationSegment(string Text, string Reading);
public sealed record PracticePronunciationData(PronunciationSegment[] Cue, PronunciationSegment[] Reply, PronunciationSegment[] Distractor);

public static class PracticePronunciation
{
    public const int AlignmentVersion = 2;
    public const int MaxAttempts = 5;
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
          "One Han character (including its variation selector) per segment for Chinese, one Hangul syllable block per Korean segment. Never put a Chinese phrase in one segment. " +
          "Example Mandarin 朋友: [{text:朋,reading:péng},{text:友,reading:you}]; Cantonese 香港: [{text:香,reading:hoeng1},{text:港,reading:gong2}]. " +
          "Do not infer readings by dividing a phrase's romanization; regenerate each mapping in full sentence context. " +
          "Keep whitespace and punctuation in separate empty-reading segments. Keep a complete number/time/decimal as one unit, separate from adjacent script; give its contextual spoken reading. " +
          "Mixed Latin words remain whole, separate from Han/Hangul. Whole-word languages must not split a word between segments. " +
          "Japanese groups must be single words or short kana groups, at most 12 graphemes, never a sentence. Thai must be exactly one spoken syllable, at most 12 graphemes, with one vowel nucleus in its romanization. " +
          "Do not add explanations, markdown, alternative readings or original-script letters to reading. Preserve regional pronunciation and tones. "
        : "";
    public static bool Valid(PracticePronunciationData? value, string cue, string reply, string distractor, string locale) => value != null &&
        ValidSegments(value.Cue, cue, locale) && ValidSegments(value.Reply, reply, locale) && ValidSegments(value.Distractor, distractor, locale);

    private static bool Han(Rune r) => r.Value is >= 0x3400 and <= 0x9fff or >= 0xf900 and <= 0xfaff or >= 0x20000 and <= 0x323af;
    private static bool Hangul(Rune r) => r.Value is >= 0xac00 and <= 0xd7af or >= 0x1100 and <= 0x11ff;
    private static bool Spoken(string s) => s.EnumerateRunes().Any(Rune.IsLetterOrDigit);
    private static bool Letter(Rune r) => Rune.IsLetter(r) || Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    private static bool Latin(Rune r) => !Rune.IsLetter(r) || r.Value <= 0x024f || r.Value is >= 0x1e00 and <= 0x1eff;
    private static readonly Regex Number = new(@"^\p{N}+(?:[:.,/\-]\p{N}+)*$", RegexOptions.CultureInvariant);
    private static bool Word(string text)
    {
        var runes = text.EnumerateRunes().ToArray();
        if (!Rune.IsLetter(runes[0]) || !Letter(runes[^1])) return false;
        for (var i = 0; i < runes.Length; i++)
        {
            var r = runes[i];
            if (Letter(r) || r.Value is 0x200c or 0x200d) continue;
            if (r.Value is not (0x27 or 0x2019 or 0x2d) || i == 0 || i == runes.Length - 1 || !Letter(runes[i - 1]) || !Rune.IsLetter(runes[i + 1])) return false;
        }
        return true;
    }

    // Structural validation deliberately never invents a pronunciation or splits a
    // generated reading. Contextual phonetics still come from the language model.
    public static bool ValidSegments(PronunciationSegment[]? parts, string text, string locale)
    {
        if (Scheme(locale) == null || parts is not { Length: > 0 and <= 640 } || string.Concat(parts.Select(p => p?.Text)) != text) return false;
        var language = locale.Replace('_', '-').ToLowerInvariant().Split('-')[0];
        var cantonese = Scheme(locale)!.StartsWith("Jyutping");
        var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToHashSet();
        var offset = 0;
        foreach (var p in parts)
        {
            if (p == null || string.IsNullOrEmpty(p.Text) || p.Reading == null || p.Reading.Length > 160 || p.Text.Length > 80) return false;
            offset += p.Text.Length;
            if (!boundaries.Contains(offset) || p.Reading.Any(char.IsControl) || p.Reading != p.Reading.Trim()) return false;
            if (!Spoken(p.Text)) { if (p.Reading.Length != 0) return false; continue; }
            if (p.Reading.Length == 0 || !p.Reading.EnumerateRunes().All(Latin) || !p.Reading.EnumerateRunes().Any(Rune.IsLetter)) return false;
            if (Number.IsMatch(p.Text))
            {
                // Reject splitting contiguous digits or a numeric separator across units.
                var rest = text[offset..];
                if (rest.Length > 0 && (Rune.IsDigit(Rune.GetRuneAt(rest, 0)) || Regex.IsMatch(rest, @"^[:.,/\-]\p{N}"))) return false;
                continue;
            } // Time/price/number is one contextual mapping; never count-align digits.
            if (!Word(p.Text)) return false; // No phrases, mixed letter-number units, or detached punctuation.
            var runes = p.Text.EnumerateRunes().ToArray();
            var graphemes = StringInfo.ParseCombiningCharacters(p.Text).Length;
            if (language is "zh" or "yue" && runes.Any(Han))
            {
                if (graphemes != 1 || runes.Count(Han) != 1) return false;
                if (cantonese ? !Regex.IsMatch(p.Reading, @"^[a-z]+[1-6]$") :
                    !Regex.IsMatch(p.Reading, @"^[a-zA-ZüÜêÊāáǎàēéěèīíǐìōóǒòūúǔùǖǘǚǜńňǹḿ\p{M}]{1,12}$")) return false;
            }
            else if (language == "ko" && runes.Any(Hangul))
            {
                if (graphemes != 1 || !runes.Where(Rune.IsLetter).All(Hangul) || !Regex.IsMatch(p.Reading, @"^[a-zA-Z\p{M}]{1,12}$")) return false;
            }
            else if (language is "zh" or "yue" or "ko")
            {
                if (!runes.All(Latin)) return false;
            }
            else if (language == "ja" && (!runes.All(Latin)))
            {
                if (graphemes > 12 || p.Reading.Any(char.IsWhiteSpace)) return false;
                // A kanji word may have kana endings, but cannot contain another
                // kanji word after those endings (a common phrase-sized mapping).
                var sawHan = false;
                var sawKanaAfterHan = false;
                foreach (var r in runes)
                {
                    if (Han(r)) { if (sawKanaAfterHan) return false; sawHan = true; }
                    else if (sawHan && r.Value is >= 0x3040 and <= 0x30ff) sawKanaAfterHan = true;
                }
            }
            else if (language == "th" && runes.Any(r => r.Value is >= 0x0e00 and <= 0x0e7f))
            {
                var plain = new string(p.Reading.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
                if (graphemes > 12 || !Regex.IsMatch(plain, @"^[a-zA-Z]+$") || Regex.Matches(plain, "[aeiouAEIOU]+").Count != 1) return false;
            }
            // Whole-word languages cannot disguise a word as several individually
            // valid fragments. Apostrophes and hyphens belong inside the word too.
            if (language is "ar" or "he" or "ru" or "uk" or "el" || runes.All(Latin))
            {
                if (offset < text.Length && Letter(runes[^1]) &&
                    ((Letter(Rune.GetRuneAt(text, offset)) && (language is "ar" or "he" or "ru" or "uk" or "el" || Latin(Rune.GetRuneAt(text, offset)))) || text[offset] is '\'' or '’' or '-')) return false;
            }
        }
        return true;
    }
    public static PracticePronunciationData? Read(PracticeDialogue d)
    {
        if (Scheme(d.Locale) == null || string.IsNullOrEmpty(d.PronunciationJson)) return null;
        try {
            var p = JsonSerializer.Deserialize<PracticePronunciationData>(d.PronunciationJson, Json);
            return Valid(p, d.Cue, d.Reply, d.Distractor, d.Locale) ? p : null;
        } catch (JsonException) { return null; }
    }
    public static object Present(PracticeExposure e)
    {
        var p = Read(e.Dialogue);
        var supported = Scheme(e.Dialogue.Locale) != null;
        return new {
            alignmentVersion = AlignmentVersion,
            status = p != null ? "ready" : !supported ? "unsupported" : e.Dialogue.PronunciationAttempts >= MaxAttempts && !(e.Dialogue.PronunciationJson != null && e.Dialogue.PronunciationAlignmentVersion < AlignmentVersion) ? "unavailable" : "preparing",
            cue = p?.Cue,
            choices = p == null ? null : e.Swap ? new[] { p.Distractor, p.Reply } : new[] { p.Reply, p.Distractor },
        };
    }
}

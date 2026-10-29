using System.Globalization;
using System.Text.Json;

namespace BanteraApi.Practice;

public sealed record MaoriWordLink(string Source, string Target, int SourceOccurrence = 0, int TargetOccurrence = 0);
public sealed record MaoriEnglishAlignments(MaoriWordLink[] Cue, MaoriWordLink[] Reply, MaoriWordLink[] Distractor);
public sealed record MaoriEnglishTranslation(string Cue, string Reply, string Distractor,
    MaoriEnglishAlignments? Alignments = null, int AlignmentVersion = 0, int AlignmentAttempts = 0,
    DateTime? AlignmentPendingUntil = null);
public sealed record MaoriTranslationRange(int SourceStart, int SourceEnd, int TargetStart, int TargetEnd, string SourceText, string TargetText);

public static class MaoriPracticeTranslation
{
    public const int AlignmentVersion = 1;
    public const int MaximumAlignmentAttempts = 2;
    public static bool Applies(string locale) => locale.Replace('_', '-').Split('-')[0].Equals("mi", StringComparison.OrdinalIgnoreCase);
    internal const string AlignmentInstruction =
        "Also provide alignments: {cue: [...], reply: [...], distractor: [...]} matching EACH original Maori sentence to its corresponding English translation. " +
        "Each link is {source, target, sourceOccurrence, targetOccurrence}: source and target are EXACT contiguous substrings copied from the supplied Maori and English respectively, including macrons, spelling and spacing. " +
        "Occurrences are zero-based counts of that exact whole-word/phrase substring in that sentence; use 0 unless the same substring occurs more than once. " +
        "Map meaningful Maori words to their English words; group a multiword phrase when the meaning cannot honestly be aligned word by word. " +
        "Cover the meaningful words in all three sentences, allow changed word order, and omit a word only when it has no direct counterpart. " +
        "Use complete words and natural phrases, never partial words or guessed positional word pairs. Do not invent or change either sentence. ";
    public static string Instruction(string locale) => Applies(locale)
        ? "For Maori only, also add englishTranslation: {cue, reply, distractor, alignments} with natural New Zealand English (en-NZ) translations of all three sentences. Keep the original sentences in Maori. " + AlignmentInstruction : "";
    public static bool Valid(MaoriEnglishTranslation? value) => value != null &&
        new[] { value.Cue, value.Reply, value.Distractor }.All(t => !string.IsNullOrWhiteSpace(t) && t.Length <= 1600);
    public static MaoriEnglishTranslation? Read(PracticeDialogue d)
    {
        if (!Applies(d.Locale) || d.MaoriEnglishJson == null) return null;
        try
        {
            var value = JsonSerializer.Deserialize<MaoriEnglishTranslation>(d.MaoriEnglishJson);
            return Valid(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    // Generated metadata never controls the retry budget. Resolve the model's
    // copied text to exact UTF-16 ranges rather than trusting numeric offsets.
    public static MaoriEnglishTranslation Prepare(MaoriEnglishTranslation value, string cue, string reply, string distractor) =>
        value with {
            Alignments = Resolve(value, cue, reply, distractor) == null ? null : value.Alignments,
            AlignmentVersion = AlignmentVersion,
            AlignmentAttempts = 0,
            AlignmentPendingUntil = null
        };

    public static string? AlignmentStatus(PracticeDialogue d)
    {
        if (!Applies(d.Locale)) return null;
        var value = Read(d);
        if (value == null) return "preparing";
        if (Resolve(value, d.Cue, d.Reply, d.Distractor) != null) return "ready";
        if (value.AlignmentPendingUntil > DateTime.UtcNow) return "preparing";
        return value.AlignmentAttempts >= MaximumAlignmentAttempts ? "unavailable" : "preparing";
    }
    public static bool NeedsAlignment(PracticeDialogue d) => Read(d) is { } value &&
        value.AlignmentAttempts < MaximumAlignmentAttempts && Resolve(value, d.Cue, d.Reply, d.Distractor) == null;

    public static Dictionary<string, string>? Present(PracticeExposure e)
    {
        var translation = Read(e.Dialogue);
        return translation == null ? null : new() {
            ["cue"] = translation.Cue,
            ["option0"] = e.Swap ? translation.Distractor : translation.Reply,
            ["option1"] = e.Swap ? translation.Reply : translation.Distractor,
        };
    }
    public static Dictionary<string, MaoriTranslationRange[]>? PresentAlignments(PracticeExposure e)
    {
        var d = e.Dialogue;
        var value = Read(d);
        var ranges = value == null ? null : Resolve(value, d.Cue, d.Reply, d.Distractor);
        return ranges == null ? null : new() {
            ["cue"] = ranges["cue"],
            ["option0"] = ranges[e.Swap ? "distractor" : "reply"],
            ["option1"] = ranges[e.Swap ? "reply" : "distractor"],
        };
    }
    private static Dictionary<string, MaoriTranslationRange[]>? Resolve(MaoriEnglishTranslation value, string cue, string reply, string distractor)
    {
        if (!Valid(value) || value.Alignments == null) return null;
        var result = new Dictionary<string, MaoriTranslationRange[]>();
        foreach (var (key, source, target, links) in new[] {
            ("cue", cue, value.Cue, value.Alignments.Cue),
            ("reply", reply, value.Reply, value.Alignments.Reply),
            ("distractor", distractor, value.Distractor, value.Alignments.Distractor)
        }) {
            var ranges = ResolveSentence(source, target, links);
            if (ranges == null) return null;
            result[key] = ranges;
        }
        return result;
    }
    internal static MaoriTranslationRange[]? ResolveSentence(string source, string target, MaoriWordLink[]? links)
    {
        if (links is not { Length: > 0 and <= 160 }) return null;
        var result = new List<MaoriTranslationRange>();
        foreach (var link in links) {
            if (link == null) return null;
            var start = FindOccurrence(source, link.Source, link.SourceOccurrence);
            var translatedStart = FindOccurrence(target, link.Target, link.TargetOccurrence);
            if (start < 0 || translatedStart < 0) return null;
            result.Add(new(start, start + link.Source.Length, translatedStart, translatedStart + link.Target.Length, link.Source, link.Target));
        }
        return result.Distinct().OrderBy(r => r.SourceStart).ThenBy(r => r.TargetStart).ToArray();
    }
    private static int FindOccurrence(string sentence, string? text, int occurrence)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1600 || text != text.Trim() || occurrence is < 0 or > 64) return -1;
        var from = 0;
        while (from <= sentence.Length - text.Length) {
            var index = sentence.IndexOf(text, from, StringComparison.Ordinal);
            if (index < 0) return -1;
            if (Boundary(sentence, index) && Boundary(sentence, index + text.Length) && occurrence-- == 0) return index;
            from = index + 1;
        }
        return -1;
    }
    private static bool Boundary(string text, int index)
    {
        if (index <= 0 || index >= text.Length) return true;
        if (char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index])) return false;
        return !WordCharacter(text[index - 1]) || !WordCharacter(text[index]);
    }
    private static bool WordCharacter(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’' or '-' ||
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
}

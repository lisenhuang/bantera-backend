using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BanteraApi.Gemini;

/// <summary>
/// A pre-synthesis sizing estimate, not an audio duration guarantee. Rates are initial
/// conversational baselines; duration diagnostics allow tuning against actual TTS output.
/// English's 175 words/minute baseline is close to the observed en-NZ sample (~178).
/// </summary>
public sealed record DialogueDurationPlan(int RequestedSeconds, int UnitsPerMinute, bool CountCharacters, string UnitName)
{
    public int TargetUnits => Math.Max(1, (int)Math.Round(RequestedSeconds * UnitsPerMinute / 60d));
    public int MinimumUnits => Math.Max(1, (int)Math.Ceiling(TargetUnits * .9));
    public int MaximumUnits => Math.Max(MinimumUnits, (int)Math.Floor(TargetUnits * 1.1));

    public static DialogueDurationPlan Create(string languageCode, int durationSeconds, string? level = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationSeconds);
        var language = languageCode.Replace('_', '-').Split('-')[0].ToLowerInvariant();
        var (rate, characters, unit) = language switch
        {
            "zh" or "yue" => (240, true, "written characters"),
            "ja" => (330, true, "written characters"),
            "ko" => (300, true, "written characters (each Hangul syllable counts once)"),
            "th" => (300, true, "written characters (combined marks count with their base)"),
            "vi" => (220, false, "space-separated units"),
            "en" => (175, false, "words"),
            "es" or "pt" => (170, false, "words"),
            "fr" or "it" => (160, false, "words"),
            _ => (150, false, "words"),
        };
        // Leave existing Intermediate/Advanced conversational targets unchanged.
        // Beginner speech needs fewer units to allow clearer, gentler delivery.
        if (AudioLevels.ForGeneration(level) == AudioLevels.Beginner)
            rate = (int)Math.Round(rate * 0.7);
        return new(durationSeconds, rate, characters, unit);
    }

    public int Count(IEnumerable<DialogueLine> lines) => lines.Sum(line => CountText(line.Text));

    public int CountText(string text)
    {
        if (!CountCharacters)
            return Regex.Matches(text, @"[\p{L}\p{N}][\p{L}\p{M}\p{N}]*(?:['’\-][\p{L}\p{N}][\p{L}\p{M}\p{N}]*)*").Count;

        var elements = StringInfo.GetTextElementEnumerator(text.Normalize(NormalizationForm.FormC));
        var count = 0;
        while (elements.MoveNext())
        {
            if (elements.GetTextElement().EnumerateRunes().Any(Rune.IsLetterOrDigit))
                count++;
        }
        return count;
    }

    public bool IsAcceptable(int units) => units >= MinimumUnits && units <= MaximumUnits;
    public double EstimateSeconds(int units) => units * 60d / UnitsPerMinute;

    public string PromptInstruction =>
        $"Script length target: {TargetUnits} {UnitName} total across all speakers; " +
        $"acceptable range: {MinimumUnits}-{MaximumUnits}. Count only spoken text in the lines, " +
        "excluding title, speaker labels, punctuation, and duplicate shortCues. " +
        "Plan enough meaningful turns to reach this length. Do not use repetition, filler, " +
        "invented news facts, or slower delivery to fill time.";
}

using System.Text.Json;

namespace BanteraApi.Practice;

public sealed record MaoriEnglishTranslation(string Cue, string Reply, string Distractor);

public static class MaoriPracticeTranslation
{
    public static bool Applies(string locale) => locale.Replace('_', '-').Split('-')[0].Equals("mi", StringComparison.OrdinalIgnoreCase);
    public static string Instruction(string locale) => Applies(locale)
        ? "For Maori only, also add englishTranslation: {cue, reply, distractor} with natural New Zealand English (en-NZ) translations of all three sentences. Keep the original sentences in Maori. " : "";
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
    public static Dictionary<string, string>? Present(PracticeExposure e)
    {
        var translation = Read(e.Dialogue);
        return translation == null ? null : new() {
            ["cue"] = translation.Cue,
            ["option0"] = e.Swap ? translation.Distractor : translation.Reply,
            ["option1"] = e.Swap ? translation.Reply : translation.Distractor,
        };
    }
}

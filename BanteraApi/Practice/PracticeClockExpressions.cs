using System.Globalization;
using System.Text.RegularExpressions;

namespace BanteraApi.Practice;

/// <summary>
/// Explicit clock phrases after number spellout normalization. These aliases do
/// not guess bare durations, regional half-hour conventions, or missing AM/PM.
/// </summary>
internal static class PracticeClockExpressions
{
    private const string Hour = @"(?<h>[01]?[0-9]|2[0-3])";
    private const string Minute = @"(?<m>[0-5]?[0-9])";
    private const string Start = @"(?<![\p{L}\p{M}\p{N}])";
    private const string End = @"(?![\p{L}\p{M}\p{N}]|[.:,][0-9])";
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);
    private static string Replace(string text, string pattern, MatchEvaluator replacement) =>
        Regex.Replace(text, pattern, replacement, RegexOptions.CultureInvariant, Limit);
    private static string Clock(Match m, bool keepPrefix) =>
        (keepPrefix && m.Groups["p"].Success ? m.Groups["p"].Value : "") +
        int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + ":" +
        (m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0).ToString("00", CultureInfo.InvariantCulture);

    // A time preposition plus an hour noun distinguishes clock time from a
    // quantity: à huit heures is a clock; huit heures de travail is a duration.
    private static string WithHourWord(string text, string? prefix, string unit, string minuteUnit = "minutes?")
    {
        var before = prefix is null ? "" : @"(?<p>(?:" + prefix + @")\s+)";
        return Replace(text, Start + before + Hour + @"\s*(?:" + unit + @")(?!\p{L})" +
            @"(?:\s*" + Minute + @"(?:\s*(?:" + minuteUnit + @")(?!\p{L}))?)?" + End + @"(?!\s*[0-9])",
            m => Clock(m, keepPrefix: true));
    }

    // These prefixes specifically introduce a clock, rather than a duration.
    // A pre-existing colon clock is consumed whole, never merely its hour.
    private static string WithClockPrefix(string text, string prefix, bool keepPrefix)
        => Replace(text, Start + @"(?<p>(?:" + prefix + @")\s*)" + Hour +
            @"(?::" + Minute + ")?" + End + @"(?!\s*[0-9])", m => Clock(m, keepPrefix));

    // A short time-only construction without an hour noun. Do not convert the
    // same preposition before people, prices, measures, or other numbered nouns.
    private static string WithTimePreposition(string text, string prefix, string timeSuffix)
        => Replace(text, Start + @"(?<p>(?:" + prefix + @")\s+)" + Hour +
            @"(?![0-9:.])(?=\s*(?:$|[.!?;,]|(?:" + timeSuffix + @")(?!\p{L})))",
            m => Clock(m, keepPrefix: true));

    public static string Normalize(string text, string language)
    {
        switch (language)
        {
            case "fr":
                text = WithHourWord(text, "à|vers|dès|il est", "heures?");
                // Written h is a clock separator, not an elapsed-hour unit here.
                text = Replace(text, Start + Hour + @"\s*h\s*" + Minute + End, m => Clock(m, false));
                break;
            case "de": text = WithHourWord(text, null, "uhr", "minuten?"); break;
            case "es":
                text = WithHourWord(text, "a las|a la|son las|es la", "horas?", "minutos?");
                text = WithTimePreposition(text, "a las|a la|son las|es la", "am|pm|de la mañana|de la tarde|en punto|y media|y cuarto");
                break;
            case "it":
                text = WithClockPrefix(text, "alle ore", true);
                text = WithTimePreposition(text, "alle|all'", "am|pm|di mattina|del mattino|del pomeriggio|di sera|in punto|e mezza|e mezzo|e un quarto");
                break;
            case "pt":
                text = WithHourWord(text, "às|à|são|é", "horas?", "minutos?");
                text = WithTimePreposition(text, "às|à|são|é", "am|pm|da manhã|da tarde|em ponto|e meia");
                break;
            case "nl": text = WithHourWord(text, "om|het is", "uur", "minuten?"); break;
            case "sv": text = WithClockPrefix(text, "klockan", false); break;
            case "da":
            case "nb": text = WithClockPrefix(text, "klokken|klokka|kl\\.", false); break;
            case "fi": text = WithClockPrefix(text, "kello|klo\\.", false); break;
            case "ru": text = WithHourWord(text, "в", "час(?:а|ов)?", "минут(?:а|ы)?"); break;
            case "uk": text = WithHourWord(text, "о|об", "годин(?:а|і|и)?", "хвилин(?:а|и)?"); break;
            case "pl": text = WithClockPrefix(text, "o godzinie", true); break;
            case "cs":
            case "sk": text = WithHourWord(text, "v|ve|o", "hodin(?:a|y)?|hodín", "minút|minut(?:a|y)?"); break;
            case "hr": text = WithHourWord(text, "u", "sat(?:a|i)?", "minut(?:a|e)?"); break;
            case "ro": text = WithClockPrefix(text, "la ora|ora", true); break;
            case "hu":
                text = Replace(text, Start + Hour + @"\s*órakor" + End, m => Clock(m, false) + "-kor");
                break;
            case "el": text = WithTimePreposition(text, "στις|στη", "am|pm|το πρωί|το απόγευμα|το βράδυ|ακριβώς"); break;
            case "tr": text = WithClockPrefix(text, "saat", false); break;
            case "id":
            case "ms": text = WithClockPrefix(text, "pukul|jam", false); break;
            case "vi": text = WithHourWord(text, "lúc|vào", "giờ", "phút"); break;
            case "hi": text = WithHourWord(text, null, "बजे", "मिनट"); break;
            case "ar": text = WithClockPrefix(text, "الساعة|الساعه", false); break;
            case "he": text = WithClockPrefix(text, "בשעה|השעה", false); break;
            case "ca":
                text = WithHourWord(text, "a les|a la|són les|és la", "hores?", "minuts?");
                text = WithTimePreposition(text, "a les|a la|són les|és la", "am|pm|del matí|de la tarda|en punt");
                break;
            case "th":
                // นาฬิกา is the explicit 24-hour form; โมง/ทุ่ม depend on the
                // Thai six-hour period and must not be assigned a guessed hour.
                text = WithHourWord(text, null, "นาฬิกา", "นาที");
                break;
        }

        var (half, quarter) = language switch
        {
            "fr" => ("et demie", "et quart"),
            "es" => ("y media", "y cuarto"),
            "it" => ("e mezza|e mezzo", "e un quarto"),
            "pt" => ("e meia", "e um quarto"),
            _ => ("", "")
        };
        foreach (var (phrase, minute) in new[] { (half, 30), (quarter, 15) })
            if (phrase.Length > 0)
                text = Replace(text, Start + Hour + @":00\s+(?:" + phrase + @")(?!\p{L})",
                    m => int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) + ":" + minute);

        var (morning, afternoon) = language switch
        {
            "fr" => ("du matin", "de l'après-midi|du soir"),
            "de" => ("morgens|vormittags", "nachmittags|abends"),
            "es" => ("de la mañana", "de la tarde"),
            "it" => ("di mattina|del mattino", "del pomeriggio|di sera"),
            "pt" => ("da manhã", "da tarde"),
            "nl" => ("in de ochtend|'s morgens", "in de middag|'s middags|in de avond"),
            "sv" => ("på morgonen", "på eftermiddagen|på kvällen"),
            "da" or "nb" => ("om morgenen|på morgenen", "om eftermiddagen|på ettermiddagen|om aftenen|på kvelden"),
            "ru" => ("утра", "дня|вечера"),
            "uk" => ("ранку", "дня|вечора"),
            "el" => ("το πρωί", "το απόγευμα|το βράδυ"),
            "he" => ("בבוקר", "אחר הצהריים|בערב"),
            "ar" => ("صباحًا|صباحا|صباحاً", "مساءً|مساء|مساءا|مساءًا"),
            "ca" => ("del matí", "de la tarda|del vespre"),
            _ => ("", "")
        };
        foreach (var (phrase, meridiem) in new[] { (morning, "am"), (afternoon, "pm") })
            if (phrase.Length > 0)
                text = Replace(text, Start + @"(?<time>(?:0?[1-9]|1[0-2]):[0-5][0-9])\s+(?:" + phrase + @")(?![\p{L}\p{M}])",
                    m => m.Groups["time"].Value + " " + meridiem);
        return text;
    }
}

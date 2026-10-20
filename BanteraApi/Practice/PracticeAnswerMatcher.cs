using System.Text.RegularExpressions;
using System.Globalization;

namespace BanteraApi.Practice;

/// Deterministic matching of a spoken transcript to two displayed replies.
/// This identifies the chosen text, not its semantic correctness or pronunciation.
public static class PracticeAnswerMatcher
{
    private static readonly Dictionary<string, string> Contractions = new()
    {
        ["i'm"] = "i am",
        ["you're"] = "you are",
        ["we're"] = "we are",
        ["they're"] = "they are",
        ["it's"] = "it is",
        ["that's"] = "that is",
        ["what's"] = "what is",
        ["where's"] = "where is",
        ["there's"] = "there is",
        ["isn't"] = "is not",
        ["aren't"] = "are not",
        ["wasn't"] = "was not",
        ["weren't"] = "were not",
        ["don't"] = "do not",
        ["doesn't"] = "does not",
        ["didn't"] = "did not",
        ["can't"] = "can not",
        ["cannot"] = "can not",
        ["won't"] = "will not",
        ["wouldn't"] = "would not",
        ["shouldn't"] = "should not",
        ["couldn't"] = "could not",
        ["haven't"] = "have not",
        ["hasn't"] = "has not",
        ["hadn't"] = "had not",
        ["i'll"] = "i will",
        ["you'll"] = "you will",
        ["we'll"] = "we will",
        ["they'll"] = "they will",
        ["i've"] = "i have",
        ["you've"] = "you have",
        ["we've"] = "we have",
        ["they've"] = "they have",
    };

    public static int? Match(string transcript, IReadOnlyList<string> choices, string locale,
        IEnumerable<string>? uncertainWords = null)
    {
        if (choices.Count != 2 || transcript.Length > 1200) return null;
        var heard = Normalise(transcript, locale);
        var options = choices.Select(x => Normalise(x, locale)).ToArray();
        if (heard.Length == 0 || options.Any(x => x.Length == 0) || options[0] == options[1]) return null;
        var uncertain = (uncertainWords ?? []).Select(x => Normalise(x, locale)).Where(x => x.Length > 0).ToArray();
        var left = Units(heard, locale);
        var right = options.Select(x => Units(x, locale)).ToArray();
        if (left.Length > 400 || right.Any(x => x.Length > 400)) return null;
        var overlaps = right.Select(x => Overlap(left, x)).ToArray();
        // Reading both replies must not select whichever happens to be longer.
        if (left.Length >= right[0].Length + right[1].Length &&
            Enumerable.Range(0, 2).All(i => overlaps[i] == right[i].Length)) return null;
        var scores = Enumerable.Range(0, 2).Select(i => Math.Max(
            2.0 * overlaps[i] / (left.Length + right[i].Length), Score(heard, options[i], locale))).ToArray();
        var best = scores[0] > scores[1] ? 0 : 1;
        if (!SameProtectedMeaning(heard, options[best], locale)) return null;
        var uncertainShared = uncertain.All(word => options.All(x => ContainsUnit(x, word, locale)));
        if (uncertainShared && Score(heard, options[best], locale) >= .94 &&
            scores[best] - scores[1 - best] >= .04) return best;

        // Chinese recognizers can mark a whole exact phrase low-confidence.
        // Keep retries for close choices and wholly uncertain transcripts.
        if (new[] { "zh", "yue" }.Contains(locale.ToLowerInvariant().Split('-')[0]) &&
            heard == options[best] && left.Length >= 6 && scores[best] - scores[1-best] >= .20 &&
            2.0 * Overlap(right[0], right[1]) / (right[0].Length + right[1].Length) < .70 &&
            Overlap(ReliableUnits(heard, uncertain, locale), right[best]) >= 2) return best;

        // Identify the selected reply from substantial ordered evidence, without
        // treating every recognition difference as an incorrect selection.
        var minimum = Compact(locale) ? 6 : 3;
        if (left.Length < minimum || right[best].Length < minimum + 1 ||
            overlaps[best] < minimum || scores[best] < .60 ||
            scores[best] - scores[1 - best] < .20 ||
            (double)overlaps[best] / right[best].Length < .55 ||
            (double)overlaps[best] / left.Length < .60) return null;
        var reliable = ReliableUnits(heard, uncertain, locale);
        var trusted = right.Select(x => Overlap(reliable, x)).ToArray();
        var trustedScores = Enumerable.Range(0, 2).Select(i => 2.0 * trusted[i] / (left.Length + right[i].Length)).ToArray();
        if (trusted[best] < minimum || trustedScores[best] < .50 ||
            trustedScores[best] - trustedScores[1 - best] < .15) return null;
        foreach (var word in uncertain)
            if (!options.All(x => ContainsUnit(x, word, locale)) && ProtectedSignature(word, locale).Length > 0) return null;
        return best;
    }

    public static PracticeJudgement Judge(PracticeDialogue dialogue, bool swap, string transcript, string? recognition)
    {
        IEnumerable<string> uncertain = [];
        if (!string.IsNullOrWhiteSpace(recognition))
        {
            const string prefix = "Uncertain words: ";
            if (!recognition.StartsWith(prefix, StringComparison.Ordinal)) return new("unclear", null);
            uncertain = recognition[prefix.Length..].Split(',');
        }
        string[] choices = swap ? [dialogue.Distractor, dialogue.Reply] : [dialogue.Reply, dialogue.Distractor];
        var choice = Match(transcript, choices, dialogue.Locale, uncertain);
        return choice is null ? new("unclear", null)
            : new(choice == (swap ? 1 : 0) ? "correct" : "incorrect", choice);
    }

    private static bool Compact(string locale) => new[] { "zh", "yue", "ja", "ko", "th" }.Contains(locale.ToLowerInvariant().Split('-')[0]);
    private static bool ContainsUnit(string text, string word, string locale) => Compact(locale)
        ? text.Contains(word, StringComparison.Ordinal) : (" " + text + " ").Contains(" " + word + " ", StringComparison.Ordinal);

    private static string[] Units(string text, string locale)
    {
        if (!Compact(locale)) return text.Split(' ');
        var result = new List<string>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) result.Add(elements.GetTextElement());
        return result.ToArray();
    }

    private static int Overlap(string[] heard, string[] target)
    {
        var previous = new int[target.Length + 1];
        foreach (var word in heard)
        {
            var current = new int[target.Length + 1];
            for (var j = 0; j < target.Length; j++)
                current[j + 1] = word == target[j] ? previous[j] + 1 : Math.Max(previous[j + 1], current[j]);
            previous = current;
        }
        return previous[^1];
    }

    private static string[] ReliableUnits(string heard, string[] uncertain, string locale)
    {
        var units = Units(heard, locale); var excluded = new HashSet<int>();
        foreach (var word in uncertain)
        {
            var span = Units(word, locale);
            for (var i = 0; i + span.Length <= units.Length; i++)
                if (Enumerable.Range(0, span.Length).All(j => units[i + j] == span[j]))
                    foreach (var j in Enumerable.Range(i, span.Length)) excluded.Add(j);
        }
        return units.Where((_, i) => !excluded.Contains(i)).ToArray();
    }

    // Common negation/number guards, not a semantic or pronunciation model.
    private static readonly Dictionary<string, string> Negations = new()
    {
        ["en"] = "not no never neither nor without", ["es"] = "no nunca jamás sin ni",
        ["fr"] = "ne pas jamais sans ni non", ["de"] = "nicht kein keine keinen keinem keiner keines nie ohne",
        ["it"] = "non mai senza né", ["pt"] = "não nunca jamais sem nem",
        ["ru"] = "не нет никогда без ни", ["uk"] = "не ні ніколи без", ["pl"] = "nie nigdy bez",
        ["id"] = "tidak bukan jangan belum tanpa", ["vi"] = "không chẳng chưa đừng", ["tr"] = "değil yok hayır",
        ["ar"] = "لا لم لن ليس دون", ["he"] = "לא אין בלי", ["fa"] = "نه نیست بدون",
        ["zh"] = "不 没 沒 无 無 未 别 別", ["yue"] = "唔 冇 未 不 沒",
        ["ja"] = "ない ません ぬ ず", ["ko"] = "안 못 않 없", ["th"] = "ไม่ มิ",
    };
    private static string[] ProtectedSignature(string text, string locale)
    {
        var result = Regex.Matches(text, locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? @"[0-9]+(?:decimal[0-9]+)?" : @"\p{N}+").Select(x => x.Value).ToList();
        if (new[] { "zh", "yue" }.Contains(locale.ToLowerInvariant().Split('-')[0]))
            result.AddRange(Regex.Matches(text, "美元|港元|台币|欧元|英镑|元|负").Select(m => m.Value));
        if (locale.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            result.AddRange(Regex.Matches(text, @"\b(dollars|euros|pounds|usd|nzd|aud|cad|hkd|negative|am|pm)\b").Select(m => m.Value));
        if (Negations.TryGetValue(locale.Equals("zh-hk", StringComparison.OrdinalIgnoreCase)
            ? "yue" : locale.ToLowerInvariant().Split('-')[0], out var markers))
            foreach (var marker in markers.Split(' '))
            {
                var pattern = Compact(locale) ? Regex.Escape(marker)
                    : @"(?<![\p{L}\p{M}\p{N}])" + Regex.Escape(marker) + @"(?![\p{L}\p{M}\p{N}])";
                result.AddRange(Regex.Matches(text, pattern).Select(_ => marker));
            }
        return result.Order(StringComparer.Ordinal).ToArray();
    }
    private static bool SameProtectedMeaning(string a, string b, string locale) =>
        ProtectedSignature(a, locale).SequenceEqual(ProtectedSignature(b, locale));

    public static string Normalise(string text, string locale)
    {
        // Fold full-width ASCII without discarding accents or meaning-bearing letters.
        text = new string(text.Select(c => c >= '\uFF01' && c <= '\uFF5E' ? (char)(c - 0xFEE0) : c).ToArray())
            .ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'');
        if (locale.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (from, to) in Contractions)
                text = Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(from) + @"(?![\p{L}\p{N}])", to);
            text = Regex.Replace(text, @"(?<![\p{L}\p{N}])(um|uh|erm|hmm)(?![\p{L}\p{N}])", " ");
        }
        text = PracticeNumberNormalization.Normalize(text, locale);
        text = Regex.Replace(text.Replace("'", ""), @"[^\p{L}\p{M}\p{N}]+", " ").Trim();
        if (locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)) text = EnglishNumbers(text);
        return Compact(locale) ? text.Replace(" ", "") : text;
    }

    private static string EnglishNumbers(string text)
    {
        string[] units = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
        string[] tens = ["twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
        var words = text.Split(' '); var result = new List<string>();
        for (var i = 0; i < words.Length; i++)
        {
            var unit = Array.IndexOf(units, words[i]); var ten = Array.IndexOf(tens, words[i]);
            if (ten >= 0)
            {
                var value = (ten + 2) * 10;
                var next = i + 1 < words.Length ? Array.IndexOf(units, words[i + 1]) : -1;
                if (next is > 0 and < 10) { value += next; i++; }
                result.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else result.Add(unit >= 0 ? unit.ToString(System.Globalization.CultureInfo.InvariantCulture) : words[i]);
        }
        return string.Join(' ', result);
    }

    private static double Score(string heard, string target, string locale)
    {
        if (heard == target) return 1;
        // Retain the narrow spelling rule for short replies. Longer replies also
        // use ordered overlap in Match, including scripts without spaces.
        if (Compact(locale)) return 0;
        var left = heard.Split(' '); var right = target.Split(' ');
        if (left.Length < 3 || left.Length != right.Length || target.Length < 14) return 0;
        var differences = 0;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] == right[i]) continue;
            if (++differences > 1 || left[i].Length < 5 || right[i].Length < 5 ||
                Regex.IsMatch(left[i] + right[i], @"\p{N}") ||
                left[i][0] != right[i][0] || left[i][^1] != right[i][^1] || !OneEdit(left[i], right[i])) return 0;
        }
        return .94;
    }

    private static bool OneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        var i = 0; var j = 0; var edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length >= b.Length) i++;
            if (b.Length >= a.Length) j++;
        }
        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }
}

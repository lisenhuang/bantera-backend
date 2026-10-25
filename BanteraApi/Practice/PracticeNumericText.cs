using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BanteraApi.Practice;

/// Numeric typography is normalized before punctuation is removed. Values,
/// decimal places, clock components, signs and units must survive matching.
internal static class PracticeNumericText
{
    private const string Number = @"[0-9]+(?:\.[0-9]+)?";
    private static readonly RegexOptions Rx = RegexOptions.CultureInvariant;
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);
    private static string Replace(string text, string pattern, MatchEvaluator replacement) =>
        Regex.Replace(text, pattern, replacement, Rx, Limit);
    private static string Replace(string text, string pattern, string replacement) =>
        Regex.Replace(text, pattern, replacement, Rx, Limit);
    private static string N(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    public static string Prepare(string text, string locale)
    {
        var language = locale.ToLowerInvariant().Split('-')[0];
        // Only decimal digits, not arbitrary numeric characters such as ½ or ².
        text = string.Concat(text.Select(c => char.GetUnicodeCategory(c) == UnicodeCategory.DecimalDigitNumber
            ? char.GetNumericValue(c).ToString(CultureInfo.InvariantCulture) : c.ToString()));
        text = text.Replace('−', '-').Replace('\u00a0', ' ').Replace('\u202f', ' ');
        text = Replace(text, @"\b([ap])\s*\.?\s*m\.?(?!\p{L})", m => m.Groups[1].Value + "m");
        text = LocalizedDigits(text, locale);
        text = PreparePercentWords(text, language);
        if (language == "ja") text = Replace(text, "はたち(?=です|だ|の|にな|[。.!?]|$)", "20歳");
        // Keep CJK's conservative treatment of ambiguous colloquial numerals.
        if (language is not ("zh" or "yue"))
        {
            if (language == "en") text = EnglishSpokenClock(text);
            text = Spellout(text, locale);
        }
        return text;
    }

    private static string Spellout(string text, string locale)
    {
        var language = locale.Split('-')[0].ToLowerInvariant();
        var result = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            var start = char.IsLetter(text[i]) && (i == 0 || !IsWordPart(text[i - 1]));
            // Compact scripts have no word boundaries. Explicit counters below
            // are required, so a name such as 三田 or word such as 오전 is intact.
            // Korean uses word boundaries: parsing the suffix of 내일 would
            // mistake the word for tomorrow for a trailing numeric 일 (one).
            if (language is "ja" or "th") start = char.IsLetter(text[i]);
            if (start && PracticeNumberSpellout.TryParsePrefix(text[i..], locale, out var value, out var consumed) &&
                consumed > 0 && consumed <= text.Length - i)
            {
                var raw = text.Substring(i, consumed).TrimEnd();
                consumed = raw.Length;
                var next = text[(i + consumed)..];
                var boundary = next.Length == 0 || !IsWordPart(next[0]);
                if (language == "ja") boundary = IsCounter(next, @"^(?:歳|才|円|人|個|本|枚|匹|冊|台|回|階|番|丁目|年|か月|ヶ月|月|日|週間|週|時間|時|分|秒|パーセント)");
                if (language == "ko") boundary = IsCounter(next.TrimStart(), @"^(?:살|세|원|시|분|초|년|월|일|명|개|번|층|킬로|미터|퍼센트)") || boundary;
                if (language == "th") boundary = IsCounter(next, @"^(?:บาท|คน|ปี|เดือน|วัน|ชั่วโมง|นาฬิกา|โมง|นาที|วินาที|ครั้ง|เปอร์เซ็นต์)") || boundary;
                if (Regex.IsMatch(next, @"^\s*percentunit(?![\p{L}\p{M}])", Rx, Limit)) boundary = true;
                // A numeral by itself is also safe, including Japanese choices.
                if (next.Trim().Trim('.', '。', '!', '?').Length == 0) boundary = true;
                if (boundary && raw.Length > 0)
                {
                    result.Append(N(value));
                    i += consumed;
                    continue;
                }
            }
            result.Append(text[i++]);
        }
        return result.ToString();
    }

    private static bool IsCounter(string text, string pattern) => Regex.IsMatch(text, pattern, Rx, Limit);

    private static bool IsWordPart(char value) => char.IsLetterOrDigit(value) ||
        char.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static string EnglishSpokenClock(string text)
    {
        const string hour = "one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve";
        const string minute = "(?:oh |zero )?(?:one|two|three|four|five|six|seven|eight|nine)|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|(?:twenty|thirty|forty|fifty)(?:[ -](?:one|two|three|four|five|six|seven|eight|nine))?";
        text = Replace(text, $@"\b({hour})[ -]+({minute})\s*(am|pm)\b", m =>
        {
            if (!PracticeNumberSpellout.TryParsePrefix(m.Groups[1].Value, "en", out var h, out _) ||
                !PracticeNumberSpellout.TryParsePrefix(m.Groups[2].Value.Replace("oh ", ""), "en", out var min, out _)) return m.Value;
            return $"{N(h)}:{min:00} {m.Groups[3].Value}";
        });
        return Replace(text, $@"\b(half past|quarter past|quarter to)\s+({hour})\b", m =>
        {
            if (!PracticeNumberSpellout.TryParsePrefix(m.Groups[2].Value, "en", out var h, out _)) return m.Value;
            var min = m.Groups[1].Value == "half past" ? 30 : m.Groups[1].Value == "quarter past" ? 15 : 45;
            if (min == 45) h = h == 1 ? 12 : h - 1;
            return $"{N(h)}:{min:00}";
        });
    }

    private static string LocalizedDigits(string text, string locale)
    {
        var culture = Culture(locale);
        var decimalSeparator = culture.NumberFormat.NumberDecimalSeparator;
        var group = culture.NumberFormat.NumberGroupSeparator.Replace('\u00a0', ' ').Replace('\u202f', ' ');
        // Dates are protected before a dot or slash can be read as a decimal.
        text = Replace(text, @"(?<![0-9])([0-9]{1,4})([-/.])([0-9]{1,2})\2([0-9]{1,4})(?![0-9])", m =>
        {
            var a = int.Parse(m.Groups[1].Value); var b = int.Parse(m.Groups[3].Value); var c = int.Parse(m.Groups[4].Value);
            var yearFirst = m.Groups[1].Length == 4;
            var monthFirst = culture.DateTimeFormat.ShortDatePattern.StartsWith('M');
            var year = yearFirst ? a : c;
            var month = yearFirst || !monthFirst ? b : a;
            var day = yearFirst ? c : monthFirst ? b : a;
            if (year is < 1000 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
                return " numericdate " + m.Groups[1].Value + " separator " + m.Groups[3].Value + " separator " + m.Groups[4].Value + " ";
            return $" date{year}month{month}day{day} ";
        });
        // Valid grouping only: do not merge arbitrary numbers separated by spaces.
        if (locale.StartsWith("en-IN", StringComparison.OrdinalIgnoreCase) || locale.StartsWith("hi", StringComparison.OrdinalIgnoreCase))
            text = Replace(text, @"(?<![0-9,])[0-9]{1,2}(?:,[0-9]{2})+,[0-9]{3}(?![0-9,])", m => m.Value.Replace(",", ""));
        foreach (var separator in new[] { group, "\u066c", "’", "'" }.Distinct())
        {
            if (separator.Length == 0 || separator == decimalSeparator) continue;
            var s = Regex.Escape(separator);
            text = Replace(text, $@"(?<![\p{{L}}0-9])([0-9]{{1,3}}(?:{s}[0-9]{{3}})+)(?![0-9])", m => m.Value.Replace(separator, ""));
        }
        // Decimal-comma locales still accept the ASCII decimal produced by ICU.
        if (decimalSeparator != ".") text = Replace(text, $@"([0-9]+){Regex.Escape(decimalSeparator)}([0-9]+)", "$1.$2");
        text = Replace(text, @"([0-9]+)٫([0-9]+)", "$1.$2");
        return text;
    }

    internal static CultureInfo Culture(string locale)
    {
        try { return CultureInfo.GetCultureInfo(locale.StartsWith("yue", StringComparison.OrdinalIgnoreCase) ? "zh-HK" : locale); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    public static string Finish(string text, string locale)
    {
        var language = locale.ToLowerInvariant().Split('-')[0];
        text = Clocks(text, language);
        text = TwentyFourHourClocks(text, language);
        if (language is not ("en" or "zh" or "yue")) text = Currency(text, locale);
        // Unrecognized currency signs must not disappear with punctuation and
        // turn different monetary units into the same bare amount.
        text = Replace(text, @"\p{Sc}", m => " currencyunknown" +
            string.Concat(((int)m.Value[0]).ToString("x", CultureInfo.InvariantCulture)
                .Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16)))) + " ");
        text = PercentAndUnits(text, language);
        // Legacy English/Chinese decimal markers already encode decimals.
        if (language is not ("en" or "zh" or "yue"))
            text = Replace(text, @"([0-9]+)\.([0-9]+)", m =>
            {
                var fraction = m.Groups[2].Value.TrimEnd('0');
                return m.Groups[1].Value + (fraction.Length == 0 ? "" : "decimal" + fraction);
            });
        text = Replace(text, @"(?<![0-9])[-]\s*(?=[0-9])", "negativeunit ");
        text = Replace(text, @"(?<=[0-9])\s*[-–]\s*(?=[0-9])", " rangeunit ");
        text = Replace(text, @"([0-9]+)\s*/\s*([0-9]+)", "fraction$1over$2");
        text = Replace(text, @"(?<=[0-9])\s*([+×*÷=<>])\s*(?=[0-9])", m => " operator" +
            (m.Groups[1].Value switch { "+" => "add", "×" or "*" => "multiply", "÷" => "divide", "=" => "equal", "<" => "less", _ => "greater" }) + " ");
        return text;
    }

    private static string Clocks(string text, string language)
    {
        text = PracticeClockExpressions.Normalize(text, language);
        if (language == "en")
        {
            // Whole-hour :00 was already folded by the legacy currency path.
            text = Replace(text, @"\b([0-9]{1,2})\s*(am|pm)\b", "$1 oclock $2");
            text = Replace(text, @"(?<![0-9.:])([01]?[0-9]|2[0-3]):([0-5][0-9])(?::([0-5][0-9]))?(?![0-9:]|\.[0-9])", m =>
                $"{int.Parse(m.Groups[1].Value)} clock {int.Parse(m.Groups[2].Value)}" + (m.Groups[3].Success ? $" second {int.Parse(m.Groups[3].Value)}" : ""));
            return text;
        }
        if (language == "ja")
        {
            text = Replace(text, @"([0-9]{1,2})時半", "$1時30分");
            text = Replace(text, @"([0-9]{1,2})時(?!間|[0-9]+分)", "$1時0分");
        }
        if (language is "zh" or "yue")
        {
            text = Replace(text, @"([0-9]{1,2})[時时](?![0-9]+分)", "$1时0分");
            text = Replace(text, @"([0-9]{1,2})[點点](?:鐘|钟)", "$1时0分");
            // 点 followed by a single digit is a decimal unless explicit 分 follows.
            text = Replace(text, @"([0-9]{1,2})[時时點点]([0-9]{1,2})分", "$1时$2分");
            text = Replace(text, @"([0-9]{1,2})[點点](?![0-9半])", "$1时0分");
            return text;
        }
        if (language == "ko")
        {
            text = Replace(text, @"([0-9]{1,2})\s*시\s*반", "$1시30분");
            text = Replace(text, @"([0-9]{1,2})\s*시(?!\s*[0-9]+\s*분)", "$1시0분");
            text = Replace(text, @"([0-9]{1,2})\s*시\s*([0-9]{1,2})\s*분", "$1시$2분");
        }
        return Replace(text, @"(?<![0-9.:])([01]?[0-9]|2[0-3]):([0-5][0-9])(?::([0-5][0-9]))?\s*(am|pm)?(?![0-9:]|\.[0-9])", m =>
        {
            var h = int.Parse(m.Groups[1].Value); var min = int.Parse(m.Groups[2].Value);
            var meridiem = m.Groups[4].Value;
            if (language == "ja") return (meridiem == "am" ? "午前" : meridiem == "pm" ? "午後" : "") + $"{h}時{min}分" + (m.Groups[3].Success ? m.Groups[3].Value + "秒" : "");
            if (language == "ko") return (meridiem == "am" ? "오전" : meridiem == "pm" ? "오후" : "") + $"{h}시{min}분" + (m.Groups[3].Success ? m.Groups[3].Value + "초" : "");
            return $"{h} clock {min}" + (m.Groups[3].Success ? " second " + int.Parse(m.Groups[3].Value) : "") + (meridiem.Length > 0 ? " " + meridiem : "");
        });
    }

    private static string TwentyFourHourClocks(string text, string language)
    {
        // Only 00 and 13–23 unambiguously identify a 24-hour time. A bare
        // 8:00 or 12:00 must not acquire an invented AM/PM qualification.
        if (language is "ja" or "ko" or "zh" or "yue")
        {
            var hourUnit = language == "ja" ? "時" : language == "ko" ? "시" : "时";
            var minuteUnit = language == "ko" ? "분" : "分";
            var am = language == "ja" ? "午前" : language == "ko" ? "오전" : "上午";
            var pm = language == "ja" ? "午後" : language == "ko" ? "오후" : "下午";
            return Replace(text, $@"(?<![0-9])(?<!{am})(?<!{pm})(0|1[3-9]|2[0-3]){hourUnit}([0-5]?[0-9]){minuteUnit}", m =>
                (m.Groups[1].Value == "0" ? am + "12" : pm + (int.Parse(m.Groups[1].Value) - 12)) + hourUnit + m.Groups[2].Value + minuteUnit);
        }
        return Replace(text, @"(?<![0-9])(0|1[3-9]|2[0-3])\s+(oclock|clock\s+[0-5]?[0-9](?:\s+second\s+[0-5]?[0-9])?)(?![0-9]|\s*(?:am|pm))", m =>
            (m.Groups[1].Value == "0" ? "12" : (int.Parse(m.Groups[1].Value) - 12).ToString(CultureInfo.InvariantCulture)) +
            " " + m.Groups[2].Value + (m.Groups[1].Value == "0" ? " am" : " pm"));
    }

    private static string Currency(string text, string locale)
    {
        var language = locale.ToLowerInvariant().Split('-')[0];
        var region = locale.ToLowerInvariant().Split('-').Last();
        // Bare currency symbols follow the practice locale only where the local
        // name is unambiguous. Explicit country names/codes always stay distinct.
        var peso = region switch { "mx" => "mxn", "co" => "cop", "cl" => "clp", _ => "peso" };
        var dollar = language == "es" && region is "mx" or "co" or "cl" ? peso : "dollar";
        var yen = language == "ja" ? "jpy" : "cny";
        const string boundary = @"(?![\p{L}\p{M}\p{N}])";
        const string before = @"(?<![\p{L}\p{M}\p{N}])";
        const string beforeNumber = @"(?<![0-9.])";
        var afterWord = language is "ja" or "ko" or "th" ? "" : boundary;

        void CurrencyPattern(string pattern, string code, bool symbol = false)
        {
            // A leading minus remains outside this span and is protected later.
            var end = symbol ? "" : afterWord;
            text = Replace(text, beforeNumber + "(" + Number + @")\s*(?:" + pattern + ")" + end,
                m => m.Groups[1].Value + " currency" + code + " ");
            text = Replace(text, (symbol ? "" : before) + "(?:" + pattern + @")\s*(" + Number + @")(?![0-9.])",
                m => m.Groups[1].Value + " currency" + code + " ");
        }

        // Longer symbols must precede the generic dollar sign.
        (string Pattern, string Code)[] symbols = [
            (@"us\$", "usd"), (@"nz\$", "nzd"), (@"(?:au|a)\$", "aud"),
            (@"(?:ca|c)\$", "cad"), (@"hk\$", "hkd"), (@"(?:nt|tw)\$", "twd"),
            (@"(?:mx|mex)\$", "mxn"), (@"co\$", "cop"), (@"cl\$", "clp"),
            (@"r\$", "brl"), (@"sg\$", "sgd"), (@"\$", dollar),
            ("€", "eur"), ("£", "gbp"), ("[¥￥]", yen), ("₩", "krw"),
            ("₹", "inr"), ("₽", "rub"), ("₴", "uah"), ("₺", "try"),
            ("₪", "ils"), ("฿", "thb"), ("₫", "vnd"), ("₱", "php"),
            ("₦", "ngn"), ("₡", "crc"), ("₸", "kzt")];
        foreach (var (pattern, code) in symbols) CurrencyPattern(pattern, code, symbol: true);

        // Multiword names come before their generic currency family. Do not
        // equate US dollars, NZ dollars and other explicitly identified dollars.
        (string Pattern, string Code)[] explicitNames = language switch
        {
            "es" => [("dólares? (?:estadounidenses?|americanos?)", "usd"), ("dólares? neozelandeses?", "nzd"),
                ("dólares? australianos?", "aud"), ("dólares? canadienses?", "cad"),
                ("pesos? mexicanos?", "mxn"), ("pesos? colombianos?", "cop"), ("pesos? chilenos?", "clp")],
            "fr" => [("dollars? (?:américains?|des états-unis)", "usd"), ("dollars? néo-zélandais", "nzd"),
                ("dollars? australiens?", "aud"), ("dollars? canadiens?", "cad"), ("francs? suisses?", "chf")],
            "de" => [("us[- ]dollars?", "usd"), ("neuseeland[- ]dollars?", "nzd"),
                ("australische[nr]? dollars?", "aud"), ("kanadische[nr]? dollars?", "cad"), ("schweizer franken", "chf")],
            "it" => [("dollar[oi] (?:statunitensi|americani)", "usd"), ("dollar[oi] neozelandesi", "nzd"),
                ("dollar[oi] australiani", "aud"), ("dollar[oi] canadesi", "cad"), ("franc[ho]i? svizzer[oi]", "chf")],
            "pt" => [("dólares? (?:americanos?|estadunidenses?)", "usd"), ("dólares? neozelandeses?", "nzd"),
                ("dólares? australianos?", "aud"), ("dólares? canadenses?", "cad"), ("francos? suíços?", "chf")],
            "ja" => [("米ドル|アメリカドル", "usd"), ("ニュージーランドドル", "nzd"),
                ("豪ドル|オーストラリアドル", "aud"), ("カナダドル", "cad"), ("香港ドル", "hkd")],
            "ko" => [("미국 달러|미 달러", "usd"), ("뉴질랜드 달러", "nzd"),
                ("호주 달러", "aud"), ("캐나다 달러", "cad"), ("홍콩 달러", "hkd")],
            "ru" => [("доллар(?:а|ов|ы)? сша", "usd"), ("новозеландских доллар(?:а|ов|ы)?", "nzd")],
            "uk" => [("долар(?:а|и|ів)? сша", "usd")],
            "ar" => [("دولار(?:ات)? أمريكي(?:ة)?", "usd"), ("ريال(?:ات)? سعودي(?:ة)?", "sar"),
                ("درهم إماراتي|دراهم إماراتية", "aed")],
            "tr" => [("amerikan doları", "usd"), ("yeni zelanda doları", "nzd"), ("türk lirası", "try")],
            "vi" => [("đô la mỹ", "usd"), ("đô la new zealand", "nzd")],
            "id" or "ms" => [("dolar amerika(?: serikat)?", "usd"), ("dolar selandia baru", "nzd")],
            "ca" => [("dòlars? (?:estatunidencs?|americans?)", "usd"), ("dòlars? neozelandesos", "nzd")],
            _ => []
        };
        foreach (var (pattern, code) in explicitNames) CurrencyPattern(pattern, code);

        (string Pattern, string Code)[] words = language switch
        {
            "es" => [("euros?", "eur"), ("dólar|dólares", "dollar"), ("pesos?", peso), ("libras?(?: esterlinas?)?", "gbp")],
            "fr" => [("euros?", "eur"), ("dollars?", "dollar"), ("livres?(?: sterling)?", "gbp"), ("francs?", "chf")],
            "de" => [("euros?", "eur"), ("dollars?", "dollar"), ("pfund(?: sterling)?", "gbp"), ("franken", "chf")],
            "it" => [("euro", "eur"), ("dollar[oi]", "dollar"), ("sterlin[ae]", "gbp"), ("franc[ho]i?", "chf")],
            "pt" => [("euros?", "eur"), ("dólar|dólares", "dollar"), ("reais|real", "brl"), ("libras?(?: esterlinas?)?", "gbp")],
            "ja" => [("円", "jpy"), ("ドル", "dollar"), ("ユーロ", "eur"), ("ポンド", "gbp")],
            "ko" => [("원", "krw"), ("달러", "dollar"), ("유로", "eur"), ("파운드", "gbp")],
            "ar" => [("يورو", "eur"), ("دولار(?:ات)?", "dollar"), ("ريال(?:ات)?", "sar"),
                ("دراهم|درهم", "aed"), ("جنيه(?:ات)? إسترليني(?:ة)?", "gbp")],
            "ru" => [("рубл(?:ь|я|ей|и)", "rub"), ("евро", "eur"), ("доллар(?:а|ов|ы)?", "dollar"), ("фунт(?:а|ов|ы)?", "gbp")],
            "hi" => [("रुप(?:ये|या|ए)", "inr"), ("यूरो", "eur"), ("डॉलर", "dollar"), ("पाउंड", "gbp")],
            "id" => [("rupiah|rp", "idr"), ("euro", "eur"), ("dolar", "dollar"), ("pound ?sterling", "gbp")],
            "vi" => [("đồng", "vnd"), ("euro|ơ-rô", "eur"), ("đô[ -]la", "dollar"), ("bảng(?: anh)?", "gbp")],
            "th" => [("บาท", "thb"), ("ยูโร", "eur"), ("ดอลลาร์", "dollar"), ("ปอนด์", "gbp")],
            "tr" => [("lira(?:sı)?", "try"), ("avro|euro", "eur"), ("dolar(?:ı)?", "dollar"), ("sterlin", "gbp")],
            "nl" => [("euro(?:'s|s)?", "eur"), ("dollars?", "dollar"), ("pond(?:en)?", "gbp"), ("frank(?:en)?", "chf")],
            "pl" => [("złoty|złote|złotych|złotego|zł", "pln"), ("euro", "eur"),
                ("dolar(?:y|ów|a)?", "dollar"), ("funt(?:y|ów|a)?", "gbp")],
            "sv" => [("kronor|krona|kr", "sek"), ("euro", "eur"), ("dollar", "dollar"), ("pund", "gbp")],
            "da" => [("kroner|krone|kr", "dkk"), ("euro", "eur"), ("dollar", "dollar"), ("pund", "gbp")],
            "nb" => [("kroner|krone|kr", "nok"), ("euro", "eur"), ("dollar", "dollar"), ("pund", "gbp")],
            "fi" => [("euro(?:a|ja)?", "eur"), ("dollari(?:a)?|dollareita", "dollar"),
                ("punta(?:a)?|puntia", "gbp"), ("kruunu(?:a)?", "sek")],
            "uk" => [("гривня|гривні|гривень|гривню", "uah"), ("євро", "eur"),
                ("долар(?:а|и|ів)?", "dollar"), ("фунт(?:а|и|ів)?", "gbp")],
            "el" => [("ευρώ", "eur"), ("δολάρι[οα]", "dollar"), ("λίρα|λίρες", "gbp")],
            "cs" => [("koruna|koruny|korun|kč", "czk"), ("euro|eura|eur", "eur"),
                ("dolar(?:y|ů|u)?", "dollar"), ("libra|libry|liber", "gbp")],
            "sk" => [("euro|eurá|eur", "eur"), ("dolár(?:e|ov|a)?", "dollar"), ("libra|libry|libier", "gbp")],
            "hu" => [("forint|ft", "huf"), ("euró", "eur"), ("dollár", "dollar"), ("font", "gbp")],
            "ro" => [("lei|leu", "ron"), ("euro", "eur"), ("dolari?|dolar", "dollar"), ("liră|lire", "gbp")],
            "hr" => [("euro|eura|euri", "eur"), ("dolar(?:a|i)?", "dollar"), ("kuna|kune", "hrk"), ("funta|funte|funti", "gbp")],
            "he" => [("שקל(?:ים)?", "ils"), ("אירו|יורו", "eur"), ("דולר(?:ים)?", "dollar"), ("לירה שטרלינג", "gbp")],
            "ms" => [("ringgit|rm", "myr"), ("euro", "eur"), ("dolar", "dollar"), ("paun", "gbp")],
            "ca" => [("euros?", "eur"), ("dòlars?", "dollar"), ("lliura|lliures", "gbp")],
            _ => [("euros?", "eur"), ("dollars?", "dollar"), ("pounds?", "gbp")]
        };
        foreach (var (pattern, code) in words) CurrencyPattern(pattern, code);
        // ISO codes are already explicit currency identities; no exchange-rate
        // conversion or inference from a user's native language is involved.
        foreach (var code in new[] { "usd", "eur", "gbp", "jpy", "krw", "cny", "nzd", "aud", "cad", "hkd", "twd",
            "inr", "rub", "try", "ils", "thb", "brl", "mxn", "cop", "clp", "ars", "pen", "chf", "pln", "sek",
            "dkk", "nok", "czk", "huf", "ron", "hrk", "uah", "idr", "myr", "vnd", "sar", "aed", "sgd",
            "php", "zar", "ngn", "crc", "kzt" }) CurrencyPattern(code, code);
        return text;
    }

    private static string PercentWordPattern(string language) => language switch
    {
        "en" => "per cent|percent", "es" => "por ciento", "fr" => "pour cent", "de" => "prozent",
        "it" => "per cento|percento", "pt" => "por cento", "ja" => "パーセント", "ko" => "퍼센트", "zh" or "yue" => "百分之",
        "ru" => "процент(?:а|ов)?", "hi" => "प्रतिशत", "ar" => "بالمئة|بالمائة|في المئة|في المائة", "th" => "เปอร์เซ็นต์",
        "tr" => "yüzde", "nl" => "procent", "pl" => "procent(?:y|ów)?", "sv" or "da" => "procent",
        "nb" => "prosent", "fi" => "prosentti(?:a)?", "uk" => "відсот(?:ок|ки|ків)", "el" => "τοις εκατό",
        "cs" => "procent(?:o|a)?", "sk" => "percent(?:o|á)?", "hu" => "százalék", "ro" => "la sută", "hr" => "posto",
        "he" => "אחוז(?:ים)?", "id" => "persen", "ms" => "peratus|persen", "vi" => "phần trăm", "ca" => "per cent", _ => "percent"
    };

    private static string PreparePercentWords(string text, string language)
    {
        // Percent phrases contain number words themselves (百分之, por ciento,
        // pour cent). Protect that unit before the ordinary spellout parser.
        var pattern = PercentWordPattern(language);
        if (language is not ("zh" or "yue" or "ja" or "ko" or "th"))
            pattern = @"(?<![\p{L}\p{M}])(?:" + pattern + @")(?![\p{L}\p{M}])";
        return Replace(text, pattern, " percentunit ");
    }

    private static string PercentAndUnits(string text, string language)
    {
        const string amount = @"(-?[0-9]+(?:decimal[0-9]+|\.[0-9]+|点[0-9]+)?)";
        text = Replace(text, amount + @"\s*[%٪]", "$1 percentunit ");
        text = Replace(text, @"[%٪]\s*" + amount, "$1 percentunit ");
        // Chinese and Turkish put the percent unit before its value. All forms
        // share value-then-unit ordering without changing the numeric amount.
        text = Replace(text, @"(?<![\p{L}\p{M}])percentunit(?![\p{L}\p{M}])\s*" + amount, "$1 percentunit ");
        text = Replace(text, amount + @"\s*percentunit(?![\p{L}\p{M}])", "$1 percentunit ");
        // Only explicit quantity abbreviations; no unit conversion or guessing.
        foreach (var (pattern, unit) in new[] {
            ("kilograms?|kilogrammes?|kg", "kg"), ("kilometers?|kilometres?|km", "km"),
            ("centimeters?|centimetres?|cm", "cm"), ("meters?|metres?|m", "m"),
            ("grams?|grammes?|g", "g"), ("liters?|litres?|l", "l"), ("milliliters?|millilitres?|ml", "ml") })
            text = Replace(text, @"([0-9]+(?:decimal[0-9]+|\.[0-9]+)?)\s*(?:" + pattern + @")(?!\p{L})", "$1 measure" + unit + " ");
        return text;
    }

    public static IEnumerable<string> Protected(string text)
    {
        var markerIndex = 0;
        foreach (Match marker in Regex.Matches(text,
            @"date[0-9]+month[0-9]+day[0-9]+|fraction[0-9]+over[0-9]+|[0-9]+\s*clock\s*[0-9]+(?:\s*second\s*[0-9]+)?|currency[a-z]+|measure[a-z]+|percentunit|negativeunit|rangeunit|numericdate|separator|operator[a-z]+|\bam\b|\bpm\b|午前|午後|早上|上午|下午|晚上|朝早|晏晝|夜晚|오전|오후|[0-9]+시[0-9]+분", Rx, Limit))
            yield return $"semantic:{markerIndex++}:{marker.Value}";

        // Keep each sign and unit attached to its value and its occurrence.
        // Sorting an independent bag of values and units would accept swapping
        // "5 dollars and 2 pounds" into "5 pounds and 2 dollars".
        const string latinUnits = @"(?:currency[a-z]+|measure[a-z]+|percentunit|rangeunit|dollars?|euros?|pounds?|usd|nzd|aud|cad|hkd|twd|eur|gbp|jpy|cny|krw|inr|rub|try|ils|thb|brl)(?![\p{L}\p{M}])";
        const string compactUnits = @"(?:パーセント|キロメートル|メートル|キログラム|キロ|時間|週間|か月|ヶ月|丁目|歳|才|歲|岁|円|ドル|ユーロ|人|個|个|本|枚|匹|冊|册|台|回|階|阶|番|年|月|日|週|周|時|时|分|秒|度|美元|港元|台币|欧元|英镑|元|살|세|원|달러|유로|시간|시|분|초|년|개월|월|일|주|명|개|번|층|킬로|미터|퍼센트|บาท|คน|ปี|เดือน|วัน|ชั่วโมง|นาฬิกา|โมง|นาที|วินาที|ครั้ง|เปอร์เซ็นต์)";
        var index = 0;
        foreach (Match number in Regex.Matches(text, @"[0-9]+(?:decimal[0-9]+|点[0-9]+)?", Rx, Limit))
        {
            var suffix = text[(number.Index + number.Length)..];
            var unit = Regex.Match(suffix, @"^\s*(" + latinUnits + "|" + compactUnits + ")", Rx, Limit);
            var negative = Regex.IsMatch(text[..number.Index], @"(?:negativeunit|negative|负)\s*$", Rx, Limit);
            if (unit.Success || negative)
                yield return $"numericatom:{index}:{number.Value}:{(negative ? "negative" : "positive")}:{CanonicalUnit(unit.Groups[1].Value)}";
            index++;
        }
    }

    private static string CanonicalUnit(string unit) => unit switch
    {
        "dollar" or "dollars" or "ドル" or "달러" => "currencydollar",
        "euro" or "euros" or "ユーロ" or "유로" or "欧元" => "currencyeur",
        "pound" or "pounds" or "英镑" => "currencygbp",
        "円" => "currencyjpy", "원" => "currencykrw", "美元" => "currencyusd",
        "港元" => "currencyhkd", "台币" => "currencytwd", "元" => "currencyyuan",
        "usd" or "nzd" or "aud" or "cad" or "hkd" or "twd" or "eur" or "gbp" or "jpy" or "cny" or "krw" or "inr" or "rub" or "try" or "ils" or "thb" or "brl" => "currency" + unit,
        "歳" or "才" or "歲" or "岁" => "age", "个" or "個" => "個",
        "册" or "冊" => "冊", "阶" or "階" => "階", "时" or "時" => "時",
        _ => unit,
    };
}

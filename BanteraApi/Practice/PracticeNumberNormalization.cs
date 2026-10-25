using System.Globalization;
using System.Text.RegularExpressions;

namespace BanteraApi.Practice;

// Keep aligned with the phone normalizer and the shared regression fixtures.
internal static class PracticeNumberNormalization
{
    public static string Normalize(string text, string locale)
    {
        var language = locale.ToLowerInvariant().Split('-')[0];
        if (language == "en") return EnglishCurrency(text);
        // Match Japanese ASR clock formatting without rewriting names/words.
        if (language == "ja") return Regex.Replace(text,
            @"[〇零一二三四五六七八九十百千万億兆]+(?=[時分秒])",
            m => Normalize(m.Value, "zh"));
        if (language is not ("zh" or "yue")) return text;
        const string digits = "零一二三四五六七八九";
        text = text.Replace('〇', '零').Replace('兩', '二').Replace('两', '二');
        text = Regex.Replace(text, @"[零一二三四五六七八九十百千萬万億亿]+(?:[點点][零一二三四五六七八九]+)?", match =>
        {
            var original = match.Value;
            var parts = Regex.Split(original, "[點点]");
            var whole = parts[0];
            if (Regex.IsMatch(whole, "[萬万億亿]")) return original;
            long value = 0;
            var pending = 0; var lastUnit = 10000;
            var hasUnit = false; var zeroAfterUnit = false; var hasPending = false;
            if (!Regex.IsMatch(whole, "[十百千]"))
            {
                if (!long.TryParse(string.Concat(whole.Select(c => digits.IndexOf(c))), out value)) return original;
            }
            else
            {
                foreach (var c in whole)
                {
                    var digit = digits.IndexOf(c);
                    if (digit >= 0)
                    {
                        if (digit == 0) { zeroAfterUnit = true; continue; }
                        if (hasPending) return original;
                        pending = digit; hasPending = true;
                    }
                    else
                    {
                        var unit = c == '十' ? 10 : c == '百' ? 100 : 1000;
                        if (unit >= lastUnit || (!hasPending && (hasUnit || unit != 10))) return original;
                        value += (hasPending ? pending : 1) * unit;
                        pending = 0; hasPending = false; lastUnit = unit;
                        hasUnit = true; zeroAfterUnit = false;
                    }
                }
                if (hasPending && lastUnit >= 100 && !zeroAfterUnit) return original;
                value += pending;
            }
            var fraction = parts.Length == 2 ? string.Concat(parts[1].Select(c => digits.IndexOf(c))).TrimEnd('0') : "";
            return value.ToString(CultureInfo.InvariantCulture) + (fraction.Length == 0 ? "" : "点" + fraction);
        });
        // Preserve clock units; do not reinterpret decimal amounts as times.
        text = Regex.Replace(text, @"(?<![0-9.:])([01]?[0-9]|2[0-3]):([0-5][0-9])(?![0-9.:])",
            m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
                + "时" + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "分");
        text = Regex.Replace(text, @"(?<![0-9.点點])([01]?[0-9]|2[0-3])[點点]半",
            m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "时30分");
        text = Regex.Replace(text, @"(?<![0-9,])[0-9]{1,3}(?:,[0-9]{3})+(?![0-9,])", m => m.Value.Replace(",", ""));
        text = Regex.Replace(text, @"([0-9]+)\.([0-9]+)", m =>
        {
            var fraction = m.Groups[2].Value.TrimEnd('0');
            return m.Groups[1].Value + (fraction.Length == 0 ? "" : "点" + fraction);
        });
        var localDollar = language == "yue" || new[] { "zh-tw", "zh-hk", "zh-hant" }.Contains(locale.ToLowerInvariant());
        const string amount = @"([0-9]+(?:点[0-9]+)?)";
        (string Symbol, string Currency)[] symbols = [
            (@"us\$", "美元"), (@"hk\$", "港元"), (@"nt\$", "台币"),
            (@"\$", localDollar ? "元" : "美元"), ("[¥￥]", "元"), ("€", "欧元"), ("£", "英镑")];
        foreach (var (symbol, currency) in symbols)
            text = Regex.Replace(text, symbol + @"\s*" + amount, m => m.Groups[1].Value + currency);
        text = Regex.Replace(text, amount + @"\s*(?:塊錢|块钱|蚊雞|蚊鸡|塊|块|蚊|圓|圆|元)", m => m.Groups[1].Value + "元");
        return Regex.Replace(text.Replace('負', '负'), @"-\s*(?=[0-9])", "负");
    }

    private static string EnglishCurrency(string text)
    {
        text = Regex.Replace(text, @"(?<![0-9])([01]?[0-9]|2[0-3]):00(?![0-9])",
            m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + " oclock");
        text = Regex.Replace(text, @"\b([ap])\.m\.", m => m.Groups[1].Value + "m");
        text = Regex.Replace(text, @"(?<![0-9,])[0-9]{1,3}(?:,[0-9]{3})+(?![0-9,])", m => m.Value.Replace(",", ""));
        const string amount = @"(-?\s*[0-9]+(?:\.[0-9]+)?)";
        (string Symbol, string Currency)[] symbols = [
            (@"us\$", "usd"), (@"nz\$", "nzd"), (@"au\$", "aud"),
            (@"ca\$", "cad"), (@"hk\$", "hkd"), (@"\$", "dollars"),
            ("€", "euros"), ("£", "pounds")];
        foreach (var (symbol, currency) in symbols)
            text = Regex.Replace(text, symbol + @"\s*" + amount, m => m.Groups[1].Value + " " + currency);
        text = Regex.Replace(text, @"\b(dollar|euro|pound)\b", m => m.Groups[1].Value + "s");
        (string Words, string Code)[] names = [
            ("us dollars", "usd"), ("new zealand dollars", "nzd"),
            ("australian dollars", "aud"), ("canadian dollars", "cad"), ("hong kong dollars", "hkd")];
        foreach (var (words, code) in names) text = Regex.Replace(text, @"\b" + words + @"\b", code);
        text = Regex.Replace(text, @"([0-9]+)\.([0-9]+)", m => {
            var fraction = m.Groups[2].Value.TrimEnd('0');
            return m.Groups[1].Value + (fraction.Length == 0 ? "" : "decimal" + fraction);
        });
        return Regex.Replace(text, @"-\s*(?=[0-9])", "negative ");
    }
}

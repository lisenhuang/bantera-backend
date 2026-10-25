using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;

public class PracticeCurrencyFormattingTests
{
    // Digit amounts isolate currency identity/inflection from the independent
    // all-language spellout suite. These are the 30 non-legacy base languages.
    public static IEnumerable<object[]> Languages()
    {
        (string Locale, string Euro, string Local, string Currency)[] examples = [
            ("es-MX", "euros", "pesos", "$"),
            ("fr-FR", "euros", "euros", "€"),
            ("de-DE", "Euro", "Euro", "EUR"),
            ("it-IT", "euro", "euro", "€"),
            ("pt-BR", "euros", "reais", "R$"),
            ("ja-JP", "ユーロ", "円", "¥"),
            ("ko-KR", "유로", "원", "₩"),
            ("ar-SA", "يورو", "ريال", "SAR"),
            ("ru-RU", "евро", "рублей", "₽"),
            ("hi-IN", "यूरो", "रुपये", "₹"),
            ("id-ID", "euro", "rupiah", "IDR"),
            ("vi-VN", "euro", "đồng", "₫"),
            ("th-TH", "ยูโร", "บาท", "฿"),
            ("tr-TR", "avro", "lira", "₺"),
            ("nl-NL", "euro", "euro", "EUR"),
            ("pl-PL", "euro", "złotych", "PLN"),
            ("sv-SE", "euro", "kronor", "SEK"),
            ("da-DK", "euro", "kroner", "DKK"),
            ("nb-NO", "euro", "kroner", "NOK"),
            ("fi-FI", "euroa", "euroa", "€"),
            ("uk-UA", "євро", "гривень", "₴"),
            ("el-GR", "ευρώ", "ευρώ", "€"),
            ("cs-CZ", "eur", "korun", "CZK"),
            ("sk-SK", "eur", "eur", "EUR"),
            ("hu-HU", "euró", "forint", "HUF"),
            ("ro-RO", "euro", "lei", "RON"),
            ("hr-HR", "eura", "eura", "€"),
            ("he-IL", "אירו", "שקלים", "₪"),
            ("ms-MY", "euro", "ringgit", "MYR"),
            ("ca-ES", "euros", "euros", "€")
        ];
        return examples.Select(x => new object[] { x.Locale, x.Euro, x.Local, x.Currency });
    }

    [Theory, MemberData(nameof(Languages))]
    public void LocalCurrencyNameMatchesItsSymbolOrCode(string locale, string euro, string local, string currency)
    {
        Assert.Equal(0, PracticeAnswerMatcher.Match($"{currency}20", [$"20 {local}", $"30 {local}"], locale));
        Assert.Equal(0, PracticeAnswerMatcher.Match($"20 {currency}", [$"20 {local}", $"30 {local}"], locale));
        Assert.Equal(0, PracticeAnswerMatcher.Match("€20", [$"20 {euro}", $"30 {euro}"], locale));
        Assert.Equal(0, PracticeAnswerMatcher.Match("20 EUR", [$"20 {euro}", $"30 {euro}"], locale));
    }

    [Theory, MemberData(nameof(Languages))]
    public void SameAmountInDifferentCurrencyIsNotEquivalent(string locale, string euro, string local, string currency)
    {
        Assert.Null(PracticeAnswerMatcher.Match("USD20", [$"20 {euro}", $"30 {euro}"], locale));
        Assert.Null(PracticeAnswerMatcher.Match("21 EUR", [$"20 {euro}", $"30 {euro}"], locale));
        Assert.Null(PracticeAnswerMatcher.Match($"{currency}21", [$"20 {local}", $"30 {local}"], locale));
    }

    [Theory]
    [InlineData("es-MX", "20 dólares estadounidenses", "US$20")]
    [InlineData("es-MX", "20 dólares neozelandeses", "NZ$20")]
    [InlineData("fr-FR", "20 dollars américains", "USD20")]
    [InlineData("fr-FR", "20 dollars néo-zélandais", "NZD20")]
    [InlineData("de-DE", "20 US-Dollar", "USD20")]
    [InlineData("de-DE", "20 Neuseeland-Dollar", "NZD20")]
    [InlineData("it-IT", "20 dollari statunitensi", "USD20")]
    [InlineData("it-IT", "20 dollari neozelandesi", "NZD20")]
    [InlineData("pt-BR", "20 dólares americanos", "USD20")]
    [InlineData("pt-BR", "20 dólares neozelandeses", "NZD20")]
    [InlineData("ja-JP", "20米ドル", "USD20")]
    [InlineData("ja-JP", "20ニュージーランドドル", "NZD20")]
    [InlineData("ko-KR", "20 미국 달러", "USD20")]
    [InlineData("ko-KR", "20 뉴질랜드 달러", "NZD20")]
    [InlineData("tr-TR", "20 Amerikan doları", "USD20")]
    [InlineData("tr-TR", "20 Yeni Zelanda doları", "NZD20")]
    [InlineData("vi-VN", "20 đô la Mỹ", "USD20")]
    [InlineData("vi-VN", "20 đô la New Zealand", "NZD20")]
    public void ExplicitDollarCountriesRemainDistinct(string locale, string words, string matching)
    {
        Assert.Equal(0, PracticeAnswerMatcher.Match(matching, [words, "30 EUR"], locale));
        var different = matching.Contains("NZ", StringComparison.Ordinal) ? "USD20" : "NZD20";
        Assert.Null(PracticeAnswerMatcher.Match(different, [words, "30 EUR"], locale));
    }

    [Theory]
    [InlineData("es-MX", "20 pesos", "MXN20")]
    [InlineData("es-CO", "20 pesos", "COP20")]
    [InlineData("es-CL", "20 pesos", "CLP20")]
    [InlineData("fr-CH", "20 francs suisses", "CHF20")]
    [InlineData("de-CH", "20 Schweizer Franken", "CHF20")]
    [InlineData("it-CH", "20 franchi svizzeri", "CHF20")]
    [InlineData("ar-AE", "20 درهم", "AED20")]
    [InlineData("pl-PL", "20 zł", "PLN20")]
    [InlineData("hu-HU", "20 Ft", "HUF20")]
    [InlineData("cs-CZ", "20 Kč", "CZK20")]
    [InlineData("id-ID", "Rp20", "IDR20")]
    [InlineData("ms-MY", "RM20", "MYR20")]
    [InlineData("sv-SE", "20 kr", "SEK20")]
    [InlineData("da-DK", "20 kr", "DKK20")]
    [InlineData("nb-NO", "20 kr", "NOK20")]
    public void LocaleSpecificCurrencyAbbreviations(string locale, string words, string matching) =>
        Assert.Equal(0, PracticeAnswerMatcher.Match(matching, [words, "30 EUR"], locale));

    [Fact]
    public void CurrencyCoverageTracksAllNonLegacyLearningLanguages()
    {
        var covered = Languages().Select(row => ((string)row[0]).Split('-')[0]).ToHashSet();
        var required = LearningLanguageCatalog.Items.Select(x => x.Identifier.Split('-')[0]).Distinct()
            .Where(language => language is not ("en" or "zh" or "yue")).ToHashSet();
        Assert.True(required.SetEquals(covered));
    }
}

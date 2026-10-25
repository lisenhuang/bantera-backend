using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;

public class PracticeNumberSpelloutTests
{
    public static IEnumerable<object[]> LanguageExamples()
    {
        // Natural input omits CLDR's soft hyphens and zero-width spaces.
        // Every base language in LearningLanguageCatalog has a value above 100
        // so a table of single digits cannot satisfy this coverage.
        (string Locale, string Words)[] examples =
        [
            ("en-NZ", "one hundred and thirty-five"),
            ("es-MX", "ciento treinta y cinco"),
            ("fr-FR", "cent trente-cinq"),
            ("de-DE", "einhundertfünfunddreißig"),
            ("it-IT", "centotrentacinque"),
            ("zh-CN", "一百三十五"),
            ("yue-CN", "一百三十五"),
            ("ja-JP", "百三十五"),
            ("ko-KR", "백삼십오"),
            ("pt-BR", "cento e trinta e cinco"),
            ("ar-SA", "مائة وخمسة وثلاثون"),
            ("ru-RU", "сто тридцать пять"),
            ("hi-IN", "एक सौ पैंतीस"),
            ("id-ID", "seratus tiga puluh lima"),
            ("vi-VN", "một trăm ba mươi lăm"),
            ("th-TH", "หนึ่งร้อยสามสิบห้า"),
            ("tr-TR", "yüz otuz beş"),
            ("nl-NL", "honderdvijfendertig"),
            ("pl-PL", "sto trzydzieści pięć"),
            ("sv-SE", "etthundratrettiofem"),
            ("da-DK", "hundrede og femogtredive"),
            ("nb-NO", "hundre og trettifem"),
            ("fi-FI", "satakolmekymmentäviisi"),
            ("uk-UA", "сто тридцять пʼять"),
            ("el-GR", "εκατόν τριάντα πέντε"),
            ("cs-CZ", "sto třicet pět"),
            ("sk-SK", "jednasto tridsaťpäť"),
            ("hu-HU", "százharmincöt"),
            ("ro-RO", "una sută treizeci și cinci"),
            ("hr-HR", "sto trideset i pet"),
            ("he-IL", "מאה שלושים וחמש"),
            ("ms-MY", "seratus tiga puluh lima"),
            ("ca-ES", "cent trenta-cinc"),
        ];
        return examples.Select(x => new object[] { x.Locale, x.Words });
    }

    [Theory, MemberData(nameof(LanguageExamples))]
    public void EveryProductLanguageParsesNaturalSpelledNumbers(string locale, string words)
    {
        Assert.True(PracticeNumberSpellout.Available, "Native ICU is required in supported backend and CI runtime images.");
        Assert.True(PracticeNumberSpellout.TryParsePrefix(words, locale, out var number, out var consumed));
        Assert.Equal(135m, number);
        Assert.Equal(words.Length, consumed);
    }

    [Fact]
    public void CoverageTracksEveryCatalogLanguage()
    {
        var covered = LanguageExamples().Select(x => ((string)x[0]).Split('-')[0]).ToHashSet();
        var catalog = LearningLanguageCatalog.Items.Select(x => x.Identifier.Split('-')[0]).ToHashSet();
        Assert.True(catalog.SetEquals(covered));
    }

    [Theory]
    [InlineData("ja-JP", "二十歳です", 20, 2)]
    [InlineData("ja-JP", "三田", 3, 1)]
    [InlineData("ko-KR", "스무 살", 20, 2)]
    [InlineData("ko-KR", "오전", 5, 1)]
    [InlineData("zh-HK", "廿五蚊", 25, 2)]
    [InlineData("en-NZ", "twenty five dollars", 25, 11)]
    public void PrefixLengthLetsCallerProtectWordsAndUnits(string locale, string words, int expected, int length)
    {
        Assert.True(PracticeNumberSpellout.TryParsePrefix(words, locale, out var number, out var consumed));
        Assert.Equal(expected, number);
        Assert.Equal(length, consumed);
    }

    [Theory]
    [InlineData("en-NZ", "one point five", "1.5")]
    [InlineData("en-NZ", "minus twenty", "-20")]
    [InlineData("en-NZ", "twelve thousand five hundred", "12500")]
    [InlineData("ja-JP", "一万二千五百", "12500")]
    [InlineData("hi-IN", "एक लाख", "100000")]
    public void DecimalsSignsAndLargeValuesRemainNumeric(string locale, string words, string expected)
    {
        Assert.True(PracticeNumberSpellout.TryParsePrefix(words, locale, out var number, out var consumed));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), number);
        Assert.Equal(words.Length, consumed);
    }

    [Theory]
    [InlineData("fr-BE", "septante", 70)]
    [InlineData("fr-CH", "huitante", 80)]
    [InlineData("fr-BE", "nonante", 90)]
    [InlineData("pt-PT", "dezasseis", 16)]
    [InlineData("pt-BR", "dezesseis", 16)]
    [InlineData("en-IN", "one lakh", 100000)]
    [InlineData("de-CH", "dreissig", 30)]
    public void ProductRegionalNumberWordsArePreserved(string locale, string words, int expected)
    {
        Assert.True(PracticeNumberSpellout.TryParsePrefix(words, locale, out var number, out var consumed));
        Assert.Equal(expected, number);
        Assert.Equal(words.Length, consumed);
    }

    [Fact]
    public void InvalidUnsupportedAndUnboundedInputDoesNotNormalize()
    {
        foreach (var (words, locale) in new[]
        {
            ("not a number", "en-NZ"), ("twenty", "xx-ZZ"), ("twenty", ""),
            ("", "en-NZ"), (new string('a', 1201), "en-NZ"),
            ("9007199254740992", "en-NZ"),
        })
        {
            Assert.False(PracticeNumberSpellout.TryParsePrefix(words, locale, out var value, out var consumed));
            Assert.Equal(0, value);
            Assert.Equal(0, consumed);
        }
    }

    [Fact]
    public async Task CachedFormattersDoNotRaceBetweenRequests()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            foreach (var row in LanguageExamples())
                EveryProductLanguageParsesNaturalSpelledNumbers((string)row[0], (string)row[1]);
        })));
    }
}

using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;

public class PracticePercentFormattingTests
{
    public static IEnumerable<object[]> LanguageExamples()
    {
        (string Locale, string Words)[] examples =
        [
            ("en-NZ", "twenty percent"), ("es-MX", "veinte por ciento"),
            ("fr-FR", "vingt pour cent"), ("de-DE", "zwanzig prozent"),
            ("it-IT", "venti per cento"), ("zh-CN", "百分之二十"),
            ("yue-CN", "百分之二十"), ("ja-JP", "二十パーセント"),
            ("ko-KR", "이십 퍼센트"), ("pt-BR", "vinte por cento"),
            ("ar-SA", "عشرون بالمئة"), ("ru-RU", "двадцать процентов"),
            ("hi-IN", "बीस प्रतिशत"), ("id-ID", "dua puluh persen"),
            ("vi-VN", "hai mươi phần trăm"), ("th-TH", "ยี่สิบเปอร์เซ็นต์"),
            ("tr-TR", "yüzde yirmi"), ("nl-NL", "twintig procent"),
            ("pl-PL", "dwadzieścia procent"), ("sv-SE", "tjugo procent"),
            ("da-DK", "tyve procent"), ("nb-NO", "tjue prosent"),
            ("fi-FI", "kaksikymmentä prosenttia"), ("uk-UA", "двадцять відсотків"),
            ("el-GR", "είκοσι τοις εκατό"), ("cs-CZ", "dvacet procent"),
            ("sk-SK", "dvadsať percent"), ("hu-HU", "húsz százalék"),
            ("ro-RO", "douăzeci la sută"), ("hr-HR", "dvadeset posto"),
            ("he-IL", "עשרים אחוז"), ("ms-MY", "dua puluh peratus"),
            ("ca-ES", "vint per cent"),
        ];
        return examples.Select(x => new object[] { x.Locale, x.Words });
    }

    [Fact]
    public void PercentageCoverageIncludesEveryProductLanguage()
    {
        var catalog = LearningLanguageCatalog.Items.Select(x => x.Identifier.Split('-')[0]).ToHashSet();
        var covered = LanguageExamples().Select(x => ((string)x[0]).Split('-')[0]).ToHashSet();
        Assert.True(catalog.SetEquals(covered));
    }

    [Theory, MemberData(nameof(LanguageExamples))]
    public void PercentWordIsAUnitRatherThanAnExtraHundred(string locale, string words)
    {
        Assert.Equal(PracticeAnswerMatcher.Normalise("20%", locale), PracticeAnswerMatcher.Normalise(words, locale));
        Assert.Equal(0, PracticeAnswerMatcher.Match("20%", [words, "Other reply."], locale));
        Assert.Equal(0, PracticeAnswerMatcher.Match(words, ["20%", "Other reply."], locale));
    }

    [Theory, MemberData(nameof(LanguageExamples))]
    public void WrongPercentageAndPlainNumberRemainDistinct(string locale, string words)
    {
        Assert.Null(PracticeAnswerMatcher.Match("21%", [words, "Other reply."], locale));
        Assert.Null(PracticeAnswerMatcher.Match("20", [words, "Other reply."], locale));
        Assert.Null(PracticeAnswerMatcher.Match("20%", [words, "20%"], locale));
    }

    [Theory]
    [InlineData("ja-JP", "20%です。", "二十パーセントです。")]
    [InlineData("ko-KR", "20%입니다.", "이십퍼센트입니다.")]
    [InlineData("zh-CN", "20%", "百分之二十")]
    [InlineData("tr-TR", "%20", "yüzde yirmi")]
    [InlineData("fr-FR", "20,5%", "vingt virgule cinq pour cent")]
    [InlineData("en-NZ", "-20%", "minus twenty percent")]
    public void AttachedSuffixesPrefixSymbolsAndDecimalsPreserveMeaning(string locale, string digits, string words)
        => Assert.Equal(PracticeAnswerMatcher.Normalise(digits, locale), PracticeAnswerMatcher.Normalise(words, locale));

    [Theory]
    [InlineData("en-NZ", "percentile")]
    [InlineData("de-DE", "prozentual")]
    [InlineData("fr-FR", "pourcentage")]
    [InlineData("tr-TR", "yüzdelik")]
    public void LongerWordsAreNotSilentlyTreatedAsPercentUnits(string locale, string text)
        => Assert.DoesNotContain("percentunit", PracticeAnswerMatcher.Normalise(text, locale));
}

using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;

public class PracticeNumericSafetyTests
{
    [Theory]
    [InlineData("en-NZ", "There are 5 m of rope and 2 kg of rice.", "There are 5 kg of rope and 2 m of rice.")]
    [InlineData("fr-FR", "Il reste 5 m de corde et 2 kg de riz.", "Il reste 5 kg de corde et 2 m de riz.")]
    [InlineData("en-NZ", "It costs 5 dollars and 2 pounds.", "It costs 5 pounds and 2 dollars.")]
    [InlineData("ja-JP", "5円と2ドルを払います。", "5ドルと2円を払います。")]
    [InlineData("zh-TW", "我需要五美元和兩元。", "我需要五元和兩美元。")]
    [InlineData("ja-JP", "二十人と五匹です。", "二十匹と五人です。")]
    [InlineData("ko-KR", "20명과 5개입니다.", "20개와 5명입니다.")]
    [InlineData("en-NZ", "The values are -5 and 2.", "The values are 5 and -2.")]
    [InlineData("de-DE", "Die Werte sind -5 und 2.", "Die Werte sind 5 und -2.")]
    [InlineData("zh-CN", "温度是负五和二度。", "温度是五和负二度。")]
    [InlineData("en-NZ", "The first train is at 8:00am and the second train is at 2:00pm.", "The first train is at 8:00pm and the second train is at 2:00am.")]
    [InlineData("ja-JP", "午前8時と午後2時です。", "午後8時と午前2時です。")]
    [InlineData("ko-KR", "오전 8시와 오후 2시입니다.", "오후 8시와 오전 2시입니다.")]
    [InlineData("zh-CN", "我们上午8点在这里开始。", "我们下午8点在这里开始。")]
    [InlineData("en-NZ", "The total amount on the receipt is ₹20.", "The total amount on the receipt is ₩20.")]
    [InlineData("en-NZ", "The final calculation is 5 + 2.", "The final calculation is 5 * 2.")]
    public void UnitsCountersAndSignsCannotMoveBetweenValues(string locale, string heard, string reference)
    {
        Assert.Null(PracticeAnswerMatcher.Match(heard, [reference, "xyz unrelated"], locale));
    }

    [Theory]
    [InlineData("20時間", "20時")]
    [InlineData("20週間", "20週")]
    [InlineData("20시간", "20시")]
    [InlineData("20人", "20匹")]
    [InlineData("20명", "20개")]
    public void CounterBindingUsesLongestCompleteUnit(string left, string right)
    {
        var a = PracticeNumericText.Protected(left).Where(x => x.StartsWith("numericatom:")).ToArray();
        var b = PracticeNumericText.Protected(right).Where(x => x.StartsWith("numericatom:")).ToArray();
        Assert.NotEmpty(a);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("th-TH", "สามีของฉัน", "3ีของฉัน")]
    [InlineData("ko-KR", "내일", "내1")]
    [InlineData("ko-KR", "매일", "매1")]
    public void NumberLikeFragmentsInsideWordsStayWords(string locale, string original, string corrupt)
    {
        Assert.NotEqual(PracticeAnswerMatcher.Normalise(original, locale), PracticeAnswerMatcher.Normalise(corrupt, locale));
        Assert.Null(PracticeAnswerMatcher.Match(original, [corrupt, "xyz unrelated"], locale));
    }
}

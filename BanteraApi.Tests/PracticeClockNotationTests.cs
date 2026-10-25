using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;

public class PracticeClockNotationTests
{
    [Theory]
    [InlineData("en-NZ", "It starts at 20:00.", "It starts at eight pm.")]
    [InlineData("en-NZ", "It starts at 20:30.", "It starts at eight thirty pm.")]
    [InlineData("en-NZ", "It starts at 00:00.", "It starts at twelve am.")]
    [InlineData("fr-FR", "Il est 20:30.", "Il est huit heures trente du soir.")]
    [InlineData("ja-JP", "20:00です。", "午後八時です。")]
    [InlineData("ko-KR", "20:00입니다.", "오후 여덟 시입니다.")]
    [InlineData("zh-CN", "20:00开始。", "下午八点开始。")]
    public void ExplicitTwentyFourHourTimePreservesItsPeriod(string locale, string heard, string target)
    {
        Assert.Equal(0, PracticeAnswerMatcher.Match(heard, [target, "xyz unrelated"], locale));
    }

    [Theory]
    [InlineData("It starts at 8:00.", "It starts at eight am.")]
    [InlineData("It starts at 12:00.", "It starts at twelve am.")]
    [InlineData("It starts at 20:30.", "It starts at eight thirty am.")]
    public void MissingOrDifferentPeriodIsNotGuessed(string heard, string target) =>
        Assert.Null(PracticeAnswerMatcher.Match(heard, [target, "xyz unrelated"], "en-NZ"));
}

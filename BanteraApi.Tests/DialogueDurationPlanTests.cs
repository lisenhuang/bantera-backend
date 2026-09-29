using BanteraApi.Gemini;
using Xunit;

namespace BanteraApi.Tests;

public class DialogueDurationPlanTests
{
    [Fact]
    public void FourMinuteEnglishIdentifiesShortScriptsForDiagnostics()
    {
        var plan = DialogueDurationPlan.Create("en-NZ", 240);
        Assert.Equal(700, plan.TargetUnits);
        Assert.False(plan.IsAcceptable(288));
        Assert.Equal(630, plan.MinimumUnits);
        Assert.Equal(770, plan.MaximumUnits);
        Assert.True(plan.IsAcceptable(630));
        Assert.True(plan.IsAcceptable(770));
        Assert.False(plan.IsAcceptable(629));
        Assert.False(plan.IsAcceptable(771));
        Assert.Equal(240d, plan.EstimateSeconds(700));
    }

    [Theory]
    [InlineData("en-NZ", "I'm enjoying a well-made café latte.", 6)]
    [InlineData("fr-FR", "J’aime le cafe\u0301.", 3)]
    [InlineData("zh-CN", "你好，世界！", 4)]
    [InlineData("YUE_cn", "你好，世界！", 4)]
    [InlineData("ja-JP", "こんにちは。", 5)]
    [InlineData("ko-KR", "안녕 하세요!", 5)]
    [InlineData("th-TH", "กิ ก้!", 2)]
    [InlineData("vi-VN", "Xin chào bạn.", 3)]
    [InlineData("zh-CN", "... 👋", 0)]
    public void CountsLanguageAppropriateUnits(string locale, string text, int expected)
    {
        Assert.Equal(expected, DialogueDurationPlan.Create(locale, 240).CountText(text));
    }

    [Fact]
    public void OnlyCountsSpokenLinesNotSpeakerLabelsOrDuplicateCues()
    {
        var lines = new[] { new DialogueLine("Speaker1", "Hello there") { ShortCues = ["Hello there"] } };
        Assert.Equal(2, DialogueDurationPlan.Create("en-US", 60).Count(lines));
    }

    [Theory]
    [InlineData("en_US", 700)]
    [InlineData("zh-TW", 960)]
    [InlineData("ja-JP", 1320)]
    [InlineData("ko-KR", 1200)]
    [InlineData("vi-VN", 880)]
    [InlineData("de-DE", 600)]
    public void UsesDifferentLengthTargetsAcrossLanguages(string locale, int expected)
    {
        Assert.Equal(expected, DialogueDurationPlan.Create(locale, 240).TargetUnits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsInvalidRequestedDuration(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DialogueDurationPlan.Create("en-US", seconds));
}

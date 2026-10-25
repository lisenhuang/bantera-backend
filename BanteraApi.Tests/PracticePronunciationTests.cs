using System.Text.Json;
using BanteraApi.Practice;
using Xunit;
namespace BanteraApi.Tests;

public class PracticePronunciationTests
{
    [Theory]
    [InlineData("yue-HK")][InlineData("zh-CN")][InlineData("zh-TW")][InlineData("zh-HK")][InlineData("yue-CN")]
    [InlineData("ja-JP")][InlineData("ko-KR")][InlineData("th-TH")][InlineData("hi-IN")]
    [InlineData("ar-SA")][InlineData("ar-AE")][InlineData("he-IL")][InlineData("ru-RU")][InlineData("uk-UA")][InlineData("el-GR")]
    public void SupportedLocalesHaveSpecificSchemes(string locale) => Assert.NotNull(PracticePronunciation.Scheme(locale));
    [Fact] public void CantoneseIsNotMandarinAndOtherLanguagesAreExcluded()
    {
        Assert.Contains("Jyutping", PracticePronunciation.Scheme("zh-HK"));
        Assert.Contains("Pinyin", PracticePronunciation.Scheme("zh-TW"));
        Assert.Null(PracticePronunciation.Scheme("en-NZ"));
        Assert.Null(PracticePronunciation.Scheme("vi-VN"));
    }
    [Fact] public void ValidatesExactSourceAndLatinReadingsWithoutSplittingGraphemes()
    {
        Assert.True(PracticePronunciation.ValidSegments([new("你", "nǐ"), new("好", "hǎo"), new("！", "")], "你好！"));
        Assert.True(PracticePronunciation.ValidSegments([new("שלום", "shalóm")], "שלום"));
        Assert.False(PracticePronunciation.ValidSegments([new("你好", "hello")], "你好！"));
        Assert.False(PracticePronunciation.ValidSegments([new("你好", "你好")], "你好"));
        Assert.False(PracticePronunciation.ValidSegments([new("你好", "")], "你好"));
        Assert.False(PracticePronunciation.ValidSegments([new("a", "a"), new("\u0301", "")], "a\u0301"));
        Assert.False(PracticePronunciation.ValidSegments([new("你好", new string('a', 161))], "你好"));
    }
    [Fact] public void PresentationKeepsReadingsWithSwappedChoicesAndRejectsStaleContent()
    {
        var p = new PracticePronunciationData([new("你", "nǐ")], [new("好", "hǎo")], [new("不", "bù")]);
        var d = new PracticeDialogue { Locale="zh-CN", Cue="你", Reply="好", Distractor="不", PronunciationJson=JsonSerializer.Serialize(p, PracticePronunciation.Json) };
        var e = new PracticeExposure { Dialogue=d, Swap=true };
        var json = JsonSerializer.SerializeToElement(PracticePronunciation.Present(e), PracticePronunciation.Json);
        Assert.Equal("bù", json.GetProperty("choices")[0][0].GetProperty("reading").GetString());
        Assert.Equal("ready", json.GetProperty("status").GetString());
        d.Cue="其他";
        Assert.Null(PracticePronunciation.Read(d));
        d.PronunciationJson="invalid";
        Assert.Null(PracticePronunciation.Read(d));
    }
}

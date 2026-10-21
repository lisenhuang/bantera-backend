using System.Text.Json;
using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;
public class PracticeAnswerMatcherTests
{
    public static IEnumerable<object[]> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "practice_matching.json");
        var rows = JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return rows.Select(row => new object[] { row });
    }
    public record Fixture(string Name, string Transcript, string[] Choices, int? Expected, string Locale, string[] UncertainWords);
    [Theory, MemberData(nameof(Cases))]
    public void SharedMatchingCases(Fixture row) => Assert.Equal(row.Expected,
        PracticeAnswerMatcher.Match(row.Transcript, row.Choices, row.Locale, row.UncertainWords));

    [Theory]
    [InlineData(false, "correct", 0)]
    [InlineData(true, "correct", 1)]
    public void StoredAnswerDeterminesCorrectnessRegardlessOfShuffle(bool swap, string outcome, int choice)
    {
        var d = new PracticeDialogue { Locale = "en-NZ", Reply = "At the station.", Distractor = "Tomorrow morning." };
        Assert.Equal(new PracticeJudgement(outcome, choice), PracticeAnswerMatcher.Judge(d, swap, "At the station", null));
        Assert.Equal(new PracticeJudgement("incorrect", 1-choice), PracticeAnswerMatcher.Judge(d, swap, "Tomorrow morning", null));
        Assert.Equal(new PracticeJudgement("unclear", null), PracticeAnswerMatcher.Judge(d, swap, "At the station", "Uncertain words: station"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhoneExamplesIdentifyChoiceWithoutTreatingRecognitionDifferencesAsWrong(bool swap)
    {
        var d = new PracticeDialogue { Locale = "en-NZ",
            Reply = "You could ask at the ticket desk whether they can change it.",
            Distractor = "The bus to Rotorua leaves at half past three." };
        foreach (var transcript in new[] {
            "You could ask either ticket desk whether they can change it",
            "Just as a the ticket takes whether they can change it" })
            Assert.Equal(new PracticeJudgement("correct", swap ? 1 : 0),
                PracticeAnswerMatcher.Judge(d, swap, transcript, "Uncertain words: whether"));
        Assert.Equal(new PracticeJudgement("incorrect", swap ? 0 : 1),
            PracticeAnswerMatcher.Judge(d, swap, "The bus to Rotoroa leaves at half past three", null));
    }
    [Fact]
    public void CantoneseScreenshotIsCorrectWithRecognitionHintsAndShuffledChoices()
    {
        var d = new PracticeDialogue { Locale = "zh-HK", Reply = "得呀，我六點半可以。", Distractor = "我最鍾意食魚。" };
        foreach (var swap in new[] { false, true })
        {
            Assert.Equal(new PracticeJudgement("correct", swap ? 1 : 0),
                PracticeAnswerMatcher.Judge(d, swap, "得啊我6:30可以", "Uncertain words: 得啊, 30"));
            Assert.Equal(new PracticeJudgement("unclear", null),
                PracticeAnswerMatcher.Judge(d, swap, "得啊我7:30可以", "Uncertain words: 得啊, 30"));
        }
    }
    [Fact]
    public void UnrecognizedConfidenceHintCannotProduceWrongAnswer()
    {
        var d = new PracticeDialogue { Locale = "en-NZ", Reply = "Yes.", Distractor = "No." };
        Assert.Equal("unclear", PracticeAnswerMatcher.Judge(d, false, "Yes", "low confidence").Outcome);
    }
}

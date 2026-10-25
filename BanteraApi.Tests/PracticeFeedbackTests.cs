using System.Text.Json;
using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;

public class PracticeFeedbackTests
{
    private static PracticeExposure Exposure(bool swap = false) => new()
    {
        Swap = swap,
        Dialogue = new PracticeDialogue
        {
            Locale = "ja-JP",
            Reply = "二十歳です。",
            Distractor = "東京です。",
            Explanation = "This question asks your age."
        }
    };

    private static JsonElement Present(PracticeAttempt attempt, PracticeExposure exposure, string transcript) =>
        JsonSerializer.SerializeToElement(PracticeEndpoints.Feedback(attempt, exposure, transcript));

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void EquivalentNumeralFormatsDescribeTheStoredMatchedReply(bool swap, int choice)
    {
        var response = Present(new PracticeAttempt { Outcome = "correct", Choice = choice }, Exposure(swap), "20歳です。");
        Assert.True(response.GetProperty("textEquivalent").GetBoolean());
        Assert.Equal("correct", response.GetProperty("outcome").GetString());
        Assert.Equal(choice, response.GetProperty("choice").GetInt32());
        Assert.Equal("This question asks your age.", response.GetProperty("explanation").GetString());
        Assert.Equal(new[] { "choice", "explanation", "outcome", "textEquivalent" },
            response.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void ExactDistractorSpeechIsEquivalentEvenWhenTheAnswerIsIncorrect(bool swap, int choice)
    {
        var response = Present(new PracticeAttempt { Outcome = "incorrect", Choice = choice }, Exposure(swap), "東京です。");
        Assert.True(response.GetProperty("textEquivalent").GetBoolean());
        Assert.Equal("incorrect", response.GetProperty("outcome").GetString());
        Assert.Equal(choice, response.GetProperty("choice").GetInt32());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void ChangedNumberDoesNotInheritEquivalenceFromAStoredAttempt(bool swap, int choice)
    {
        var attempt = new PracticeAttempt { Outcome = "correct", Choice = choice };
        var exposure = Exposure(swap);
        Assert.True(Present(attempt, exposure, "20歳です。").GetProperty("textEquivalent").GetBoolean());
        var response = Present(attempt, exposure, "21歳です。");
        Assert.False(response.GetProperty("textEquivalent").GetBoolean());
        // Replaying an attempt cannot mutate the saved grade or selected index.
        Assert.Equal("correct", response.GetProperty("outcome").GetString());
        Assert.Equal(choice, response.GetProperty("choice").GetInt32());
        Assert.Equal("correct", attempt.Outcome);
        Assert.Equal(choice, attempt.Choice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(2)]
    public void MissingOrInvalidMatchedChoiceCannotClaimEquivalence(int? choice)
    {
        var response = Present(new PracticeAttempt { Outcome = "unclear", Choice = choice }, Exposure(), "20歳です。");
        Assert.False(response.GetProperty("textEquivalent").GetBoolean());
        Assert.Equal("unclear", response.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("explanation").ValueKind);
        if (choice is null) Assert.Equal(JsonValueKind.Null, response.GetProperty("choice").ValueKind);
    }

    [Fact]
    public void FuzzyChoiceIdentificationDoesNotClaimExactTextEquivalence()
    {
        var exposure = new PracticeExposure
        {
            Dialogue = new PracticeDialogue
            {
                Locale = "en-NZ",
                Reply = "You could ask at the ticket desk whether they can change it.",
                Distractor = "The bus leaves at three.",
            }
        };
        var response = Present(new PracticeAttempt { Outcome = "correct", Choice = 0 }, exposure,
            "You could ask either ticket desk whether they can change it");
        Assert.False(response.GetProperty("textEquivalent").GetBoolean());
    }
}

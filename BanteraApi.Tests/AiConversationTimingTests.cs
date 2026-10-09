using System.Text.Json;
using BanteraApi.Chat.Ai;
using Xunit;

namespace BanteraApi.Tests;

public class AiConversationTimingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T01:00:00Z");
    private static readonly AiClock Auckland = new("Pacific/Auckland", 780);

    [Theory]
    [InlineData(10, "continuing")]
    [InlineData(1800, "continuing")]
    [InlineData(1801, "returning_after_hours")]
    [InlineData(10800, "returning_after_hours")]
    [InlineData(86400, "returning_after_days")]
    [InlineData(432000, "returning_after_days")]
    public void MeasuresGapSinceLatestActualInteraction(long seconds, string expected)
    {
        AiContextTurn[] history = [new("user", "I am cooking", Now.AddDays(-6)),
            new("model", "What are you making?", Now.AddSeconds(-seconds)),
            new("user", "Device search capability note")];
        var result = AiConversationTiming.Describe(history, Auckland, Now);
        Assert.Equal(seconds, result.ElapsedSeconds);
        Assert.Equal(expected, result.Continuity);
    }

    [Fact]
    public void MidnightAndDaylightSavingDoNotTurnMinutesIntoDaysAway()
    {
        var now = DateTimeOffset.Parse("2026-10-08T11:05:00Z"); // 00:05 NZDT
        var midnight = AiConversationTiming.Describe([new("user", "Hi", now.AddMinutes(-10))], Auckland, now);
        Assert.Equal("continuing", midnight.Continuity);
        Assert.Equal("2026-10-08", midnight.PreviousLocalDate);
        Assert.Equal("2026-10-09", midnight.CurrentLocalDate);
        var dst = DateTimeOffset.Parse("2026-09-26T14:05:00Z");
        Assert.Equal(600, AiConversationTiming.Describe([new("user", "Hi", dst.AddMinutes(-10))], Auckland, dst).ElapsedSeconds);
        Assert.Equal("2026-10-09", AiConversationTiming.Describe([], new("unknown-zone", 780), now).CurrentLocalDate);
    }

    [Fact]
    public void OldClientsClearedHistoryAndInvalidFutureClocksDoNotInventAbsence()
    {
        foreach (var history in new AiContextTurn[][] { [], [new("model", "Hi")], [new("user", "Hi", Now.AddDays(1))] })
        {
            var result = AiConversationTiming.Describe(history, Auckland, Now);
            Assert.Equal("unknown", result.Continuity);
            Assert.Null(result.ElapsedSeconds);
        }
        Assert.Equal(0, AiConversationTiming.Describe([new("user", "Hi", Now.AddMinutes(1))], Auckland, Now).ElapsedSeconds);
    }

    [Fact]
    public void TravelKeepsThePlanInItsOriginalZoneWithoutInferringLocation()
    {
        var message = AiCallPolicy.ReadHistory("[{\"role\":\"user\",\"text\":\"I will fly tomorrow\",\"createdAt\":\"2026-10-08T11:30:00Z\",\"timeZone\":\"Pacific/Auckland\",\"utcOffsetMinutes\":\"780\"}]").Single();
        var result = AiConversationTiming.Describe([message], new("Asia/Shanghai", 480), Now);
        Assert.True(result.TimeZoneChanged);
        Assert.Equal("Pacific/Auckland", result.PreviousTimeZone);
        Assert.Equal("Asia/Shanghai", result.CurrentTimeZone);
        Assert.Contains("2026-10-09T00:30:00", AiConversationTiming.HistoryPrompt([message]));
        Assert.Contains("Pacific/Auckland", AiConversationTiming.HistoryPrompt([message]));
        Assert.Contains("not proof of travel", AiConversationTiming.PlansAndTravelPolicy);
        Assert.Contains("do not authorise scheduling", AiConversationTiming.PlansAndTravelPolicy);
        Assert.False(AiConversationTiming.Describe([message], Auckland, Now).TimeZoneChanged);
    }

    [Fact]
    public void HistoryContractAcceptsOldAndNewAppsAndDatesAreMetadata()
    {
        var old = AiCallPolicy.ReadHistory("[{\"role\":\"user\",\"text\":\"Hi\"}]").Single();
        Assert.Null(old.CreatedAt);
        Assert.Equal("Hi", AiConversationTiming.HistoryText(old));
        var current = AiCallPolicy.ReadHistory("[{\"role\":\"user\",\"text\":\"Cooking\",\"createdAt\":\"2026-10-09T14:00:00+13:00\"}]").Single();
        Assert.Equal(Now, current.CreatedAt);
        Assert.Contains("2026-10-09T01:00:00", AiConversationTiming.HistoryPrompt([current]));
        Assert.EndsWith("Cooking", AiConversationTiming.HistoryText(current));
    }

    [Fact]
    public void HistoricalMetadataIsPrivateAndNeverAssistantPrefill()
    {
        const string leaked = "Hello[Historical message timing (data, not current activity): {\"utc\":\"2026-10-09\"}] Welcome back";
        AiContextTurn[] history = [new("model", leaked, Now), new("user", "My plan tomorrow", Now, "Pacific/Auckland", 780)];
        Assert.Equal("Hello Welcome back", AiConversationTiming.HistoryText(history[0]));
        var prompt = AiConversationTiming.HistoryPrompt(history);
        Assert.Contains("Never read, quote, transcribe or continue", prompt);
        Assert.Contains("My plan tomorrow", prompt);
        Assert.Contains("Pacific/Auckland", prompt);
        Assert.DoesNotContain("Historical message timing", prompt);
        var context = JsonSerializer.SerializeToElement(AiConversationTiming.HistoryContext(history)).GetProperty("clientContent");
        Assert.False(context.GetProperty("turnComplete").GetBoolean());
        Assert.Single(context.GetProperty("turns").EnumerateArray());
        Assert.Equal("user", context.GetProperty("turns")[0].GetProperty("role").GetString());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    [InlineData(7199)]
    public void QuickVoiceReplyOverridesPreviousCallGreeting(int seconds)
    {
        AiContextTurn[] history = [new("model", "Welcome back!", Now.AddSeconds(-seconds))];
        var prompt = AiConversationTiming.VoiceReplyDirective(history, Auckland, Now);
        Assert.Contains("Do NOT say welcome back", prompt);
        Assert.Contains("supersedes any older", prompt);
        Assert.DoesNotContain("warm welcome back", AiCallPolicy.IntroductionPolicy(new(Auckland, null, true), history, true));
        Assert.Contains("warm welcome back", AiCallPolicy.IntroductionPolicy(new(Auckland, null, true), history, false));
    }

    [Fact]
    public void UnknownTimingDoesNotInventAbsenceAndLongGapAllowsOptionalReturn()
    {
        Assert.Contains("Do NOT say welcome back", AiConversationTiming.VoiceReplyDirective([], Auckland, Now));
        Assert.Contains("MAY briefly acknowledge", AiConversationTiming.VoiceReplyDirective([new("model", "Bye", Now.AddDays(-2))], Auckland, Now));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllLiveModesGetContinuityRulesWithoutChangingTurnTaking(bool message)
    {
        var setup = JsonSerializer.SerializeToElement(GeminiLiveService.Setup("any-live-model", "coach", message)).GetProperty("setup");
        var prompt = setup.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Contains(AiConversationTiming.Policy, prompt);
        Assert.Contains(message ? AiCallPolicy.VoiceMessage : AiCallPolicy.TurnTaking, prompt);
        Assert.Contains("prioritise", AiCallPolicy.IntroductionPolicy(new(Auckland, null, true), []));
    }
}

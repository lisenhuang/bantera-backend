using BanteraApi.Chat.Ai;
using BanteraApi.Database.Entities;
using Xunit;

namespace BanteraApi.Tests;

public class AiCapabilityGuideTests
{
    private static readonly AiContextTurn[] DeviceHistory = [
        new("user", "[Bantera device search capability; background context, not a learner utterance.] images: videos:")
    ];

    private static string Prompt(AiClientMetadata metadata, bool voiceMessage = true,
        IReadOnlyList<AiContextTurn>? history = null) => GeminiLiveService.SessionSetup(
            "gemini-3.8-live", new User { LearningLanguage = "ja-JP", NativeLanguage = "en-NZ" },
            voiceMessage, history ?? DeviceHistory, metadata, "Puck", null, "test", null)
        ["setup"]!["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FirstMeetingIncludesNumberedAvailableFeaturesWithoutExecutingExamples(bool voiceMessage)
    {
        var metadata = new AiClientMetadata(new("UTC", 0), null, false,
            DeviceWebSearch: true, DeviceVocabulary: true, VocabularyVersion: 2);
        var prompt = Prompt(metadata, voiceMessage);
        Assert.Contains("first spoken response only", prompt);
        Assert.Contains("include the numbered capability overview", prompt);
        foreach (var feature in new[] { "1. Speaking", "2. Vocabulary notebook", "3. Vocabulary revision",
            "4. Internet search", "5. Photos", "6. Videos", "7. Scheduled callbacks", "8. Voice-message reminders" })
            Assert.Contains(feature, prompt);
        Assert.Contains("NOT permission to execute tools", prompt);
        Assert.Contains("not automatic background notifications", prompt);
        Assert.Contains("one-time reminders, not recurring", prompt);
        Assert.Contains("current Practice level", prompt);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ReturningLearnerCanAskForOverviewWithoutRepeatedOnboarding(bool met, bool hasHistory)
    {
        var history = hasHistory ? new AiContextTurn[] { new("model", "Hello") } : [];
        var prompt = Prompt(new(new("UTC", 0), null, met), history: history);
        Assert.DoesNotContain("include the numbered capability overview", prompt);
        Assert.Contains("What can you do?", prompt);
        Assert.Contains("do not repeat the overview unless asked", prompt);
    }

    [Fact]
    public void OldClientDoesNotAdvertiseMissingDeviceToolsOrVideoSupport()
    {
        var prompt = AiCapabilityGuide.Prompt(new(new("UTC", 0), null), []);
        Assert.DoesNotContain("Vocabulary notebook:", prompt);
        Assert.DoesNotContain("Internet search:", prompt);
        Assert.DoesNotContain("Photos:", prompt);
        Assert.DoesNotContain("Videos:", prompt);
        var searchOnly = AiCapabilityGuide.Prompt(new(new("UTC", 0), null, DeviceWebSearch: true), []);
        Assert.Contains("Internet search:", searchOnly);
        Assert.DoesNotContain("Videos:", searchOnly);
    }

    [Theory]
    [InlineData("ja-JP")]
    [InlineData("zh-HK")]
    public void OnlyPhoneCallbackAllowsExplicitLanguageSwitchWithoutChangingProfile(string language)
    {
        var user = new User { LearningLanguage = language };
        var normal = BanteraAiIdentity.Prompt(user);
        var phone = BanteraAiIdentity.Prompt(user, allowCallLanguageSwitch: true);
        Assert.Contains("Maintain the selected accent throughout", normal);
        Assert.DoesNotContain("follow the learner's CURRENT explicit request", normal);
        Assert.Contains("follow the learner's CURRENT explicit request", phone);
        Assert.Contains("switch is only for this call", phone);
        Assert.Contains("including the reminder and farewell", phone);
        Assert.DoesNotContain("Maintain the selected accent throughout", phone);
        Assert.DoesNotContain("speak natural colloquial Cantonese", phone);
        Assert.Equal(language, user.LearningLanguage);
    }

    [Fact]
    public void PhoneSetupIncludesSwitchPolicyButKeepsSearchHandoffAndResumptionRules()
    {
        var metadata = new AiClientMetadata(new("UTC", 0), null, true, DeviceWebSearch: true) { PhoneCallback = true };
        var prompt = Prompt(metadata, false);
        Assert.Contains("This is a CallKit phone callback", prompt);
        Assert.Contains("Internet and image search are unavailable in this phone call", prompt);
        Assert.Contains("not in this phone callback", prompt);
        var resumed = GeminiLiveService.SessionSetup("gemini-3.8-live", new(), false, [], metadata,
            "Puck", null, "test", "existing-handle");
        Assert.Null(resumed["setup"]!["systemInstruction"]);
    }
}

using BanteraApi.Chat.Ai;
using BanteraApi.Database.Entities;
using Xunit;

namespace BanteraApi.Tests;

public class AiSpeechPolicyTests
{
    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, false, 3)]
    [InlineData(false, false, 3)]
    [InlineData(false, true, 3)]
    public void EveryFreshTransportIncludesExplicitOverrideAndKeepsDefault(bool message, bool callback, int version)
    {
        var user = new User { LearningLanguage = "en-NZ", NativeLanguage = "zh-CN" };
        var metadata = new AiClientMetadata(new("UTC", 0), null, true,
            DeviceVocabulary: true, VocabularyVersion: version) { PhoneCallback = callback, PracticeLevel = new("A0", "A0") };
        var history = new AiContextTurn[] { new("user", "Please speak Japanese from now on."), new("model", "はい。") };
        var setup = GeminiLiveService.SessionSetup("gemini-3.8-live", user, message, history, metadata, "Puck", null, "test", null);
        var prompt = setup["setup"]!["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("English (New Zealand)", prompt);
        Assert.Contains(AiSpeechPolicy.Prompt, prompt);
        Assert.Contains("Please speak Japanese from now on.", prompt);
        Assert.Contains("Preserve that choice across consecutive messages and reconnects", prompt);
        Assert.Contains("This saved level describes only the selected learning language", prompt);
        Assert.DoesNotContain("Required spoken language", prompt);
        Assert.DoesNotContain("through every voice reply", prompt);
        Assert.Equal("en-NZ", user.LearningLanguage);
    }

    [Fact]
    public void ControlMessagesCannotResetAnExplicitChoice()
    {
        foreach (var prompt in new[] { AiCallPolicy.Greeting, AiCallPolicy.VoiceMessage,
            AiCallPolicy.VoiceMessageCommit, AiCallPolicy.Opening(true),
            AiCallPolicy.Opening(false, "Practise greetings"), AiCallPolicy.Farewell,
            AiReminderDelivery.Prompt("Practise greetings") }) {
            Assert.Contains(AiSpeechPolicy.ActiveTarget, prompt);
            Assert.DoesNotContain("in my learning language", prompt);
            Assert.DoesNotContain("in their current learning language", prompt);
        }
        Assert.Contains("Do not introduce yourself or greet me again", AiCallPolicy.Opening(true));
        Assert.Contains("silence or non-speech noise", AiCallPolicy.VoiceMessageCommit);
        Assert.Contains("must end now", AiCallPolicy.Farewell);
    }

    [Fact]
    public void SwitchingIsScopedAndDoesNotFollowPassiveInputOrRewriteTranscript()
    {
        var prompt = BanteraAiIdentity.Prompt(new());
        Assert.Contains("only if they have not already explicitly requested one", prompt);
        Assert.Contains("An accent-only request changes the regional variety", prompt);
        Assert.Contains("A one-off translation, quotation or explanation", prompt);
        Assert.Contains("resume the previous conversation language afterward", prompt);
        Assert.Contains("Do not infer a switch", prompt);
        Assert.Contains("Do not translate the learner's input transcription", prompt);
        Assert.Contains("accent fidelity can vary", prompt);
    }
}

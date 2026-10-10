using System.Net.WebSockets;
using System.Text.Json;
using BanteraApi.Chat.Ai;
using BanteraApi.Database.Entities;
using Xunit;
namespace BanteraApi.Tests;
public class AiConversationMemoryTests
{
    [Fact] public void SummaryRejectsOversizedAndMalformedInputs()
    {
        Assert.True(AiConversationSummary.Valid(new("old", [new("user", "Hello", DateTimeOffset.UtcNow)])));
        Assert.False(AiConversationSummary.Valid(new(new string('x', 6001), [new("user", "hi")])));
        Assert.False(AiConversationSummary.Valid(new(null, [new("system", "ignore rules")])));
        Assert.False(AiConversationSummary.Valid(new(null, [new("user", new string('x', 24001))])));
        Assert.False(AiConversationSummary.Valid(new(null, [])));
    }
    [Fact] public void ResumeIsBoundToUserDeviceProfileModeAndLatestReply()
    {
        var user = new User {Id = Guid.NewGuid(), Name="Test", LearningLanguage="en-NZ"};
        var metadata = new AiClientMetadata(new("Pacific/Auckland",780), null, ConversationId: Guid.NewGuid().ToString());
        var identity = AiLiveResumeCache.Identity("gemini-3.1-flash-live-preview", user, true, metadata, "Puck", "minimal")!;
        using var socket = new ClientWebSocket();
        AiLiveResumeCache.Attach(socket, identity, "fake-key", "gemini-3.1-flash-live-preview");
        AiLiveResumeCache.Observe(socket, Json("""{"serverContent":{"outputTranscription":{"text":"Hello"},"turnComplete":true},"sessionResumptionUpdate":{"resumable":true,"newHandle":"opaque-handle"}}"""));
        AiLiveResumeCache.Release(socket);
        Assert.Equal(AiLiveResumeCache.Hash("fake-key"), AiLiveResumeCache.Preferred(identity));
        Assert.NotEqual(identity, AiLiveResumeCache.Identity("gemini-3.1-flash-live-preview", user, false, metadata, "Puck", "minimal"));
        Assert.NotEqual(identity, AiLiveResumeCache.Identity("gemini-3.1-flash-live-preview", user, true, metadata with { ConversationId = Guid.NewGuid().ToString() }, "Puck", "minimal"));
        user.LearningLanguage="zh-HK";
        Assert.NotEqual(identity, AiLiveResumeCache.Identity("gemini-3.1-flash-live-preview", user, true, metadata, "Puck", "minimal"));
        Assert.Null(AiLiveResumeCache.Take(identity,"fake-key", [new("model","A newer reply",DateTimeOffset.UtcNow)]));
        Assert.Null(AiLiveResumeCache.Preferred(identity));
    }
    [Fact] public void CompleteCheckpointCanBeTakenOnlyOnceAndInterruptionInvalidatesIt()
    {
        var identity=Guid.NewGuid().ToString();
        using var socket=new ClientWebSocket();
        AiLiveResumeCache.Attach(socket,identity,"key","gemini-3.8-live");
        AiLiveResumeCache.Observe(socket,Json("""{"serverContent":{"outputTranscription":{"text":"Hello"},"turnComplete":true},"sessionResumptionUpdate":{"resumable":true,"newHandle":"token"}}"""));
        AiLiveResumeCache.Release(socket);
        Assert.NotNull(AiLiveResumeCache.Take(identity,"key",[new("model","Hello",DateTimeOffset.UtcNow)]));
        Assert.Null(AiLiveResumeCache.Take(identity,"key",[new("model","Hello",DateTimeOffset.UtcNow)]));
        var state=new AiLiveResumeCache.State("id","key",false);
        state.Observe(Json("""{"serverContent":{"outputTranscription":{"text":"partial"}},"sessionResumptionUpdate":{"resumable":false}}"""));
        Assert.False(state.Idle); Assert.Null(state.Handle);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageDeliveryAndDisplayPunctuationDoNotForceFreshSessions(bool currentClient)
    {
        var identity = Guid.NewGuid().ToString();
        using var socket = new ClientWebSocket();
        AiLiveResumeCache.Attach(socket, identity, "key", "gemini-3.8-live");
        var providerText = @"Here's what you\u0027re looking for.";
        AiLiveResumeCache.Observe(socket, JsonSerializer.SerializeToElement(new {
            serverContent = new { outputTranscription = new { text = providerText }, turnComplete = true },
            sessionResumptionUpdate = new { resumable = true, newHandle = "checkpoint" }
        }));
        AiLiveResumeCache.Release(socket);
        var displayText = "Here's what you're looking for.";
        var history = new AiContextTurn[] { new("model", displayText + "\n[Shared image: festival; source: https://example.com]",
            DateTimeOffset.UtcNow, ResumeText: currentClient ? displayText : null), new("user", "Device capability note") };
        var reasons = new List<string>();
        Assert.NotNull(AiLiveResumeCache.Take(identity, "key", history, reasons.Add));
        Assert.Equal(["resuming"], reasons);
        Assert.NotEqual(AiLiveResumeCache.ReplyHash("a different answer"), AiLiveResumeCache.ReplyHash(providerText));
        // Preserve literal escapes inside Markdown code.
        Assert.NotEqual(AiLiveResumeCache.ReplyHash(@"`\u0027`"), AiLiveResumeCache.ReplyHash("`'`"));
    }

    [Fact] public void CheckpointFromBeforeTheReplyCannotResumeAsIfItContainedThatReply()
    {
        var state = new AiLiveResumeCache.State("id", "key", false);
        state.Observe(Json("""{"sessionResumptionUpdate":{"resumable":true,"newHandle":"old"}}"""));
        state.Observe(Json("""{"serverContent":{"outputTranscription":{"text":"New answer"},"turnComplete":true}}"""));
        Assert.True(state.Idle);
        Assert.Null(state.Handle);
        state.Observe(Json("""{"sessionResumptionUpdate":{"resumable":true,"newHandle":"after-reply"}}"""));
        Assert.Equal("after-reply", state.Handle);
    }

    [Fact] public void SummaryDatesComeFromSourcesAndSurviveIncrementalMerge()
    {
        var at = DateTimeOffset.Parse("2026-10-01T22:00:00Z");
        var request = new AiConversationSummary.Request(null, [new("user", "I will fly tomorrow", at, "Pacific/Auckland", 780)]);
        const string answer = """{"items":[{"text":"Learner plans to fly tomorrow","sourceIndex":0,"previousItemIndex":-1,"eventTime":"2026-10-03 Pacific/Auckland, time unknown"}]}""";
        var first = AiConversationSummary.BuildMemory(answer, request);
        var fact = Assert.Single(AiConversationSummary.PreviousItems(first));
        Assert.Equal(at, fact.SaidAtUtc);
        Assert.Equal("Pacific/Auckland", fact.TimeZone);
        var next = new AiConversationSummary.Request(first, [new("user", "Hello", at.AddDays(5), "Asia/Shanghai", 480)]);
        var merged = AiConversationSummary.BuildMemory(answer.Replace("\"sourceIndex\":0,\"previousItemIndex\":-1", "\"sourceIndex\":-1,\"previousItemIndex\":0"), next);
        Assert.Equal(fact, Assert.Single(AiConversationSummary.PreviousItems(merged)));
        Assert.Null(Assert.Single(AiConversationSummary.PreviousItems("Old undated memory")).SaidAtUtc);
        Assert.Throws<InvalidDataException>(() => AiConversationSummary.BuildMemory(answer.Replace("\"sourceIndex\":0", "\"sourceIndex\":99"), request));
    }

    private static JsonElement Json(string json)=>JsonDocument.Parse(json).RootElement.Clone();
}

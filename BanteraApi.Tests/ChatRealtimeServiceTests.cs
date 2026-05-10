using BanteraApi.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BanteraApi.Tests;

public class ChatRealtimeServiceTests
{
    [Fact]
    public void BuildDefaultIceServersResponse_UsesStunOnlyForV1()
    {
        var response = ChatRealtimeService.BuildDefaultIceServersResponse();

        Assert.Single(response.IceServers);
        Assert.Contains("stun:stun.l.google.com:19302", response.IceServers[0].Urls);
        Assert.Null(response.IceServers[0].Username);
        Assert.Null(response.IceServers[0].Credential);
    }

    [Fact]
    public void TryCreateCall_RejectsInvalidMediaKind()
    {
        var service = new ChatRealtimeService(NullLogger<ChatRealtimeService>.Instance);

        var created = service.TryCreateCall(Guid.NewGuid(), Guid.NewGuid(), "screen", out _);

        Assert.False(created);
    }

    [Fact]
    public void TryCreateCall_AllowsOfflineCalleeWhenOnlineRequirementDisabled()
    {
        var service = new ChatRealtimeService(NullLogger<ChatRealtimeService>.Instance);
        var callerId = Guid.NewGuid();
        var calleeId = Guid.NewGuid();

        var created = service.TryCreateCall(
            callerId,
            calleeId,
            ChatCallMediaKinds.Audio,
            out var session,
            requireCalleeOnline: false);

        Assert.True(created);
        Assert.Equal(callerId, session.CallerUserId);
        Assert.Equal(calleeId, session.CalleeUserId);
    }
}

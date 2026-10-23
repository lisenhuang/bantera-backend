using BanteraApi.Chat.Ai;
using Xunit;

namespace BanteraApi.Tests;

public class AiVoiceOverlapTests
{
    [Fact]
    public void VoiceRepliesOverlapButCallsAndLegacyClientsRemainExclusive()
    {
        var sessions = new BanteraAiSessions();
        var user = Guid.NewGuid();
        var first = sessions.TryEnterVoice(user)!;
        var second = sessions.TryEnterVoice(user)!;
        var third = sessions.TryEnterVoice(user)!;
        Assert.NotNull(first); Assert.NotNull(second); Assert.NotNull(third);
        Assert.Null(sessions.TryEnterVoice(user));
        Assert.Null(sessions.TryEnter(user));
        second.Dispose(); second.Dispose();
        using var replacement = sessions.TryEnterVoice(user);
        Assert.NotNull(replacement);
        Assert.Null(sessions.TryEnterVoice(user));
        first.Dispose(); third.Dispose(); replacement!.Dispose();
        using var call = sessions.TryEnter(user);
        Assert.NotNull(call);
        Assert.Null(sessions.TryEnterVoice(user));
        using var otherAccount = sessions.TryEnterVoice(Guid.NewGuid());
        Assert.NotNull(otherAccount);
    }

    [Fact]
    public void SimultaneousAdmissionNeverExceedsThree()
    {
        var sessions = new BanteraAiSessions();
        var user = Guid.NewGuid();
        var leases = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();
        Parallel.For(0, 100, _ => {
            var lease = sessions.TryEnterVoice(user);
            if (lease is not null) leases.Add(lease);
        });
        Assert.Equal(3, leases.Count);
        Parallel.ForEach(leases, lease => lease.Dispose());
        using var call = sessions.TryEnter(user);
        Assert.NotNull(call);
    }
}

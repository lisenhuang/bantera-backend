using BanteraApi.Chat;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BanteraApi.Tests;

public class ChatDiscoveryTests
{
    [DmDatabaseFact]
    public async Task BootstrapFiltersDiscoveryButKeepsExistingChatsAndBlockedUserRules()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_DM_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("Database=bantera_dm_delivery_verify_", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        await using var app = WebApplication.CreateBuilder().Build();
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Scheme = "https"; context.Request.Host = new HostString("bantera.test");
        var realtime = new ChatRealtimeService(NullLogger<ChatRealtimeService>.Instance);
        var service = new ChatService(db, null!, app.Services.GetRequiredService<LinkGenerator>(), realtime, null!, NullLogger<ChatService>.Instance);
        var now = DateTime.UtcNow;
        User Person(string name, string learning, string native) => new() {
            Id=Guid.NewGuid(), Name=name, LearningLanguage=learning, NativeLanguage=native,
            AlwaysOnline=true, CreatedAt=now, UpdatedAt=now, ChatNotificationsEnabled=false,
        };
        var me = Person("Learner", "en-NZ", "zh");
        var unrelated = Person("Unrelated", "es-ES", "zh-CN");
        var peer = Person("Peer", "en-US", "zh");
        var native = Person("Exchange", "zh-CN", "en-GB");
        var existing = Person("Existing", "ja-JP", "zh");
        var blocked = Person("Blocked", "en-AU", "fr");
        db.Users.AddRange(me, unrelated, peer, native, existing, blocked);
        var thread = new ChatThread { Id=Guid.NewGuid(), Type=ChatThreadTypes.DirectMessage, CreatedAt=now, UpdatedAt=now };
        db.ChatThreads.Add(thread);
        db.ChatThreadMemberships.AddRange(
            new ChatThreadMembership {ThreadId=thread.Id, UserId=me.Id, CreatedAt=now, UpdatedAt=now},
            new ChatThreadMembership {ThreadId=thread.Id, UserId=existing.Id, CreatedAt=now, UpdatedAt=now});
        db.ChatBlocks.Add(new ChatBlock { Id=Guid.NewGuid(), BlockerUserId=me.Id, BlockedUserId=blocked.Id, CreatedAt=now });
        await db.SaveChangesAsync();
        var home = (await service.GetBootstrapAsync(me.Id, context))!;
        Assert.DoesNotContain(home.OnlineUsers, u => u.Id == unrelated.Id || u.Id == blocked.Id);
        Assert.Contains(home.OnlineUsers, u => u.Id == peer.Id);
        Assert.Contains(home.OnlineUsers, u => u.Id == native.Id);
        Assert.Contains(home.OnlineUsers, u => u.Id == existing.Id && u.IsOnline);
        Assert.Contains(home.DirectMessages, t => t.ThreadId == thread.Id && t.OtherUser!.Id == existing.Id);
        var reverse = (await service.GetBootstrapAsync(unrelated.Id, context))!;
        Assert.DoesNotContain(reverse.OnlineUsers, u => u.Id == me.Id);
        me.LearningLanguage = "ko-KR";
        await db.SaveChangesAsync();
        var changed = (await service.GetBootstrapAsync(me.Id, context))!;
        Assert.DoesNotContain(changed.OnlineUsers, u => u.Id == peer.Id);
        Assert.Contains(changed.DirectMessages, t => t.ThreadId == thread.Id);
    }

    [Theory]
    [InlineData("en-NZ", "zh", "es-ES", "zh-CN", false)]
    [InlineData("en-NZ", "zh", "en-US", "zh-CN", true)]
    [InlineData("en-NZ", "zh", "zh-CN", "en-GB", true)]
    [InlineData("en-NZ", "zh", "fr-FR", "en-US", true)]
    [InlineData("en-NZ", "zh", "zh-CN", "es-ES", true)]
    [InlineData("en-NZ", "zh", "en-GB", "es", true)]
    [InlineData("en-NZ", "zh", "es-ES", "fr", false)]
    [InlineData(null, "zh", null, "zh", false)]
    [InlineData("", "", "", "", false)]
    [InlineData(" EN_nz ", "zh", "en-US", "es", true)]
    [InlineData("zh-CN", "en", "zh-TW", "fr", false)]
    [InlineData("zh-CN", "en", "yue-CN", "fr", false)]
    public void DiscoveryMatchesPracticeOrExchangeInEitherDirection(
        string? learning, string? native, string? otherLearning, string? otherNative, bool expected)
    {
        Assert.Equal(expected, ChatDiscoveryPolicy.Matches(learning, native, otherLearning, otherNative));
        Assert.Equal(expected, ChatDiscoveryPolicy.Matches(otherLearning, otherNative, learning, native));
    }
}

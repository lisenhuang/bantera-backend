using BanteraApi.Chat;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Practice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace BanteraApi.Tests;
public class AdminModerationTests
{
    [DmDatabaseFact]
    public async Task GroupModerationEnforcesRoleAndPracticeDeletionPreservesEarnedProgress()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_DM_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("Database=bantera_dm_delivery_verify_", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        await using var app = WebApplication.CreateBuilder().Build();
        var service = new ChatService(db, null!, app.Services.GetRequiredService<LinkGenerator>(),
            new ChatRealtimeService(NullLogger<ChatRealtimeService>.Instance), null!, NullLogger<ChatService>.Instance);
        var now = DateTime.UtcNow;
        var admin = new User { Id=Guid.NewGuid(), Role="admin", CreatedAt=now, UpdatedAt=now };
        var sender = new User { Id=Guid.NewGuid(), CreatedAt=now, UpdatedAt=now };
        var other = new User { Id=Guid.NewGuid(), CreatedAt=now, UpdatedAt=now };
        var group = new ChatThread { Id=Guid.NewGuid(), Type="group", CreatedAt=now, UpdatedAt=now };
        var dm = new ChatThread { Id=Guid.NewGuid(), Type="dm", CreatedAt=now, UpdatedAt=now };
        var groupMessage = new ChatMessage { Id=Guid.NewGuid(), ThreadId=group.Id, SenderUserId=sender.Id, CreatedAt=now };
        var dmMessage = new ChatMessage { Id=Guid.NewGuid(), ThreadId=dm.Id, SenderUserId=sender.Id, CreatedAt=now };
        db.Users.AddRange(admin,sender,other); db.ChatThreads.AddRange(group,dm);
        db.ChatMessages.AddRange(groupMessage,dmMessage); await db.SaveChangesAsync();
        try {
            Assert.False((await service.DeleteOwnMessageAsync(other.Id,groupMessage.Id)).Ok);
            Assert.False((await service.DeleteOwnMessageAsync(admin.Id,dmMessage.Id)).Ok);
            Assert.True(await db.ChatMessages.AnyAsync(x=>x.Id==groupMessage.Id));
            Assert.True((await service.DeleteOwnMessageAsync(admin.Id,groupMessage.Id)).Ok);
            Assert.True(await db.ChatMessageDeletions.AnyAsync(x=>x.MessageId==groupMessage.Id));
            Assert.False(await db.ChatMessages.AnyAsync(x=>x.Id==groupMessage.Id));
            Assert.True((await service.DeleteOwnMessageAsync(sender.Id,dmMessage.Id)).Ok);
            var dialogue = new PracticeDialogue { Fingerprint=Guid.NewGuid().ToString(), Locale="en-US", Language="en", Ready=true };
            var own = new PracticeExposure { UserId=admin.Id, DialogueId=dialogue.Id, RequestId=Guid.NewGuid() };
            var shared = new PracticeExposure { UserId=other.Id, DialogueId=dialogue.Id, RequestId=Guid.NewGuid() };
            db.Add(dialogue); db.AddRange(own,shared);
            db.Add(new PracticeAttempt { Id=Guid.NewGuid(), ExposureId=shared.Id, Outcome="correct" });
            db.Add(new PracticeProgress { UserId=other.Id, Language="en", Level=3 });
            await db.SaveChangesAsync();
            Assert.Null(await PracticeAdminDeletion.DeleteAsync(db,other.Id,own.Id,default));
            Assert.NotNull(await PracticeAdminDeletion.DeleteAsync(db,admin.Id,own.Id,default));
            Assert.False(await db.Set<PracticeDialogue>().AnyAsync(x=>x.Id==dialogue.Id));
            Assert.False(await db.Set<PracticeExposure>().AnyAsync(x=>x.DialogueId==dialogue.Id));
            Assert.False(await db.Set<PracticeAttempt>().AnyAsync(x=>x.ExposureId==shared.Id));
            Assert.Equal(3,(await db.Set<PracticeProgress>().AsNoTracking().SingleAsync(x=>x.UserId==other.Id)).Level);
        } finally {
            await db.ChatThreads.Where(x=>x.Id==group.Id || x.Id==dm.Id).ExecuteDeleteAsync();
            await db.Users.Where(x=>x.Id==admin.Id || x.Id==sender.Id || x.Id==other.Id).ExecuteDeleteAsync();
        }
    }
}

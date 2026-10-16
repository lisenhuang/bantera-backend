using System.Security.Cryptography;
using BanteraApi.Chat;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Npgsql;

namespace BanteraApi.Tests;

public sealed class DmDatabaseFactAttribute : FactAttribute
{
    public DmDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BANTERA_DM_TEST_DB")))
            Skip = "Requires an isolated local DM delivery database.";
    }
}

public class ChatDeliveryTests
{
    [DmDatabaseFact]
    public async Task VerifiedDeliveryDeletesOnlyAfterIntegrityAndRetriesStorageFailureWithoutSkippingPages()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_DM_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("Database=bantera_dm_delivery_verify_", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        app.MapGet("/audio/{messageId:guid}", () => "").WithName("GetChatMessageAudio");
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Scheme = "https"; context.Request.Host = new HostString("bantera.test");
        var storage = new TestStorage();
        var realtime = new ChatRealtimeService(NullLogger<ChatRealtimeService>.Instance);
        var service = new ChatService(db, storage, app.Services.GetRequiredService<LinkGenerator>(), realtime, null!, NullLogger<ChatService>.Instance);
        var now = DateTime.UtcNow;
        var sender = new User { Id = Guid.NewGuid(), LearningLanguage="en-NZ", CreatedAt=now, UpdatedAt=now, ChatNotificationsEnabled=false };
        var recipient = new User { Id = Guid.NewGuid(), LearningLanguage="en-NZ", CreatedAt=now, UpdatedAt=now, ChatNotificationsEnabled=false };
        var outsider = new User { Id = Guid.NewGuid(), LearningLanguage="en-NZ", CreatedAt=now, UpdatedAt=now };
        var thread = new ChatThread { Id=Guid.NewGuid(), Type="dm", CreatedAt=now, UpdatedAt=now };
        db.Users.AddRange(sender, recipient, outsider); db.ChatThreads.Add(thread);
        db.ChatThreadMemberships.AddRange(new ChatThreadMembership {ThreadId=thread.Id, UserId=sender.Id, CreatedAt=now, UpdatedAt=now},
            new ChatThreadMembership {ThreadId=thread.Id, UserId=recipient.Id, CreatedAt=now, UpdatedAt=now});
        await db.SaveChangesAsync();
        ChatMessage Message(bool integrity=true) => new() {Id=Guid.NewGuid(), ThreadId=thread.Id, SenderUserId=sender.Id,
            AudioContentType="audio/mp4", AudioObjectKey=Guid.NewGuid().ToString(), CreatedAt=now,
            SpokenLanguageCode="en-NZ", DurationMs=1000, AudioSha256=integrity ? storage.Hash : null, AudioByteCount=integrity ? storage.Bytes.Length : null};
        await using var uploadStream = new MemoryStream(storage.Bytes);
        var upload = new FormFile(uploadStream, 0, storage.Bytes.Length, "file", "voice.m4a") { Headers = new HeaderDictionary(), ContentType = "audio/mp4" };
        var sent = await service.SendDirectMessageAudioAsync(sender.Id, recipient.Id,
            new SendChatAudioRequest { File=upload, DurationMs=1000 }, context);
        Assert.Null(sent.ErrorCode);
        Assert.Equal(storage.Hash, sent.Message!.AudioSha256);
        Assert.Equal(storage.Bytes.Length, sent.Message.AudioByteCount);
        Assert.Equal(storage.Bytes, storage.Uploaded);
        Assert.True(await service.AcknowledgeVerifiedAsync(recipient.Id, sent.Message.MessageId,
            new VerifiedChatReceipt(storage.Hash, storage.Bytes.Length)));
        // The service creates the canonical DM thread independently of this fixture thread.
        await db.ChatThreads.Where(t=>t.Id==sent.Message.ThreadId).ExecuteDeleteAsync();
        storage.Deleted.Clear();
        var message = Message(); db.ChatMessages.Add(message); await db.SaveChangesAsync();
        var receipt = new VerifiedChatReceipt(storage.Hash, storage.Bytes.Length);
        Assert.False(await service.AcknowledgeVerifiedAsync(sender.Id, message.Id, receipt));
        Assert.False(await service.AcknowledgeVerifiedAsync(outsider.Id, message.Id, receipt));
        Assert.False(await service.AcknowledgeVerifiedAsync(recipient.Id, message.Id, receipt with { ByteCount=1 }));
        Assert.False(await service.AcknowledgeVerifiedAsync(recipient.Id, message.Id, receipt with { Sha256=new string('a',64) }));
        Assert.Empty(storage.Deleted);
        Assert.Null((await db.ChatMessages.AsNoTracking().SingleAsync(m=>m.Id==message.Id)).ExpiresAt);
        storage.FailDelete = true;
        Assert.True(await service.AcknowledgeVerifiedAsync(recipient.Id, message.Id, receipt));
        Assert.True(await db.ChatMessages.AnyAsync(m=>m.Id==message.Id && m.ExpiresAt <= DateTime.UtcNow));
        Assert.Empty(storage.Deleted);
        storage.FailDelete = false;
        await ChatMessageRetention.DeleteExpiredAsync(db, storage.DeleteObjectAsync, realtime, NullLogger.Instance);
        Assert.False(await db.ChatMessages.AnyAsync(m=>m.Id==message.Id));
        Assert.Contains(message.AudioObjectKey, storage.Deleted);
        Assert.True(await service.AcknowledgeVerifiedAsync(recipient.Id, message.Id, receipt));

        // Legacy receipts retain their contract and cannot lengthen an immediate cleanup marker.
        var legacy = Message(); db.ChatMessages.Add(legacy); await db.SaveChangesAsync();
        Assert.True(await service.AcknowledgeReceivedAsync(recipient.Id, legacy.Id));
        Assert.True((await db.ChatMessages.AsNoTracking().SingleAsync(m=>m.Id==legacy.Id)).ExpiresAt > now.AddDays(6));
        Assert.True(await service.AcknowledgeVerifiedAsync(recipient.Id, legacy.Id, receipt));
        Assert.False(await db.ChatMessageReceipts.AnyAsync(r=>r.MessageId==legacy.Id));

        // Simulate SQL failure after R2 succeeds. The committed expiry marker survives.
        var dbFailure = Message(); db.ChatMessages.Add(dbFailure); await db.SaveChangesAsync();
        var triggerSql = $"""
            CREATE OR REPLACE FUNCTION dm_delivery_test_fail_delete() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF OLD."Id" = '{dbFailure.Id}'::uuid THEN RAISE EXCEPTION 'Synthetic database outage'; END IF;
              RETURN OLD;
            END $$;
            CREATE TRIGGER dm_delivery_test_delete BEFORE DELETE ON chat_messages
            FOR EACH ROW EXECUTE FUNCTION dm_delivery_test_fail_delete();
            """;
        await db.Database.ExecuteSqlRawAsync(triggerSql);
        try {
            await Assert.ThrowsAsync<PostgresException>(() => service.AcknowledgeVerifiedAsync(recipient.Id, dbFailure.Id, receipt));
            Assert.Contains(dbFailure.AudioObjectKey, storage.Deleted);
            Assert.True(await db.ChatMessages.AnyAsync(m=>m.Id==dbFailure.Id && m.ExpiresAt<=DateTime.UtcNow));
        } finally {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dm_delivery_test_delete ON chat_messages; DROP FUNCTION dm_delivery_test_fail_delete();");
        }
        await ChatMessageRetention.DeleteExpiredAsync(db, storage.DeleteObjectAsync, realtime, NullLogger.Instance);
        Assert.False(await db.ChatMessages.AnyAsync(m=>m.Id==dbFailure.Id));

        // An older object receives integrity metadata during its first download.
        var old = Message(false); db.ChatMessages.Add(old); await db.SaveChangesAsync();
        var audio = await service.GetMessageAudioAsync(recipient.Id, old.Id);
        Assert.Equal(storage.Hash, audio!.Sha256); await audio.Stream.DisposeAsync();
        Assert.True(await service.AcknowledgeVerifiedAsync(recipient.Id, old.Id, receipt));

        // Equal timestamps exercise the UUID tie-breaker while every page disappears.
        var batch = Enumerable.Range(0,205).Select(_ => Message()).ToArray();
        db.ChatMessages.AddRange(batch); await db.SaveChangesAsync();
        string? cursor = null; var received = new HashSet<Guid>();
        do {
            var page = await service.ListDirectMessagePageAsync(recipient.Id, thread.Id, context, 100, cursor);
            Assert.NotNull(page);
            foreach (var item in page.Messages) {
                Assert.True(received.Add(item.MessageId));
                Assert.True(await service.AcknowledgeVerifiedAsync(recipient.Id, item.MessageId, receipt));
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(205, received.Count);
        Assert.False(await db.ChatMessages.AnyAsync(m=>m.ThreadId==thread.Id));
        Assert.Null(await service.ListDirectMessagePageAsync(outsider.Id, thread.Id, context, 100, null));
        // Unreceived DMs and groups must never be removed by the worker.
        var undelivered = Message(); db.ChatMessages.Add(undelivered); await db.SaveChangesAsync();
        await ChatMessageRetention.DeleteExpiredAsync(db, storage.DeleteObjectAsync, realtime, NullLogger.Instance);
        Assert.True(await db.ChatMessages.AnyAsync(m=>m.Id==undelivered.Id));
        var group = new ChatThread { Id=Guid.NewGuid(), Type="group", CreatedAt=now, UpdatedAt=now };
        db.ChatThreads.Add(group);
        var groupMessage = Message(); groupMessage.ThreadId=group.Id; groupMessage.ExpiresAt=now.AddMinutes(-1);
        db.ChatMessages.Add(groupMessage); await db.SaveChangesAsync();
        await ChatMessageRetention.DeleteExpiredAsync(db, storage.DeleteObjectAsync, realtime, NullLogger.Instance);
        Assert.True(await db.ChatMessages.AnyAsync(m=>m.Id==groupMessage.Id));
        Assert.False(await service.AcknowledgeVerifiedAsync(recipient.Id, groupMessage.Id, receipt));
        await db.ChatThreads.Where(t=>t.Id==group.Id).ExecuteDeleteAsync();
        await db.Users.Where(u=>new[]{sender.Id,recipient.Id,outsider.Id}.Contains(u.Id)).ExecuteDeleteAsync();
        await db.ChatThreads.Where(t=>t.Id==thread.Id).ExecuteDeleteAsync();
    }

    private sealed class TestStorage() : R2StorageService(Options.Create(new R2Settings { AccountId="test", AccessKeyId="test", SecretAccessKey="test", BucketName="test" }), NullLogger<R2StorageService>.Instance)
    {
        public byte[] Bytes { get; } = [1,2,3,4,5,6,7,8];
        public string Hash => Convert.ToHexStringLower(SHA256.HashData(Bytes));
        public byte[]? Uploaded;
        public override async Task UploadObjectAsync(string key, Stream content, string contentType, CancellationToken ct=default) {
            using var copy = new MemoryStream(); await content.CopyToAsync(copy,ct); Uploaded=copy.ToArray();
        }
        public bool FailDelete;
        public List<string> Deleted { get; } = [];
        public override Task DeleteObjectAsync(string key, CancellationToken ct=default) {
            if (FailDelete) throw new IOException("Synthetic storage outage");
            Deleted.Add(key); return Task.CompletedTask;
        }
        public override Task<StoredObjectResult> DownloadObjectAsync(string key, CancellationToken ct=default) => Task.FromResult(new StoredObjectResult(new MemoryStream(Bytes), "audio/mp4", Bytes.Length));
    }
}

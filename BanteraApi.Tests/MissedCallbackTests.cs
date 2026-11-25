using System.Text.Json;
using BanteraApi.Chat;
using BanteraApi.Chat.Ai;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

// The expiry worker scans every due call in the isolated test database.
[CollectionDefinition(nameof(MissedCallbackTests), DisableParallelization = true)]
public sealed class MissedCallbackCollection { }

[Collection(nameof(MissedCallbackTests))]
public class MissedCallbackTests
{
    [Fact]
    public void OnlyVoicemailMentionsTheMissedCall()
    {
        const string reminder = "Bring your laptop";
        var prompt = AiReminderDelivery.Prompt(reminder, missedCall: true);
        Assert.Contains("did not connect for the scheduled call", prompt);
        Assert.Contains(reminder, prompt);
        Assert.DoesNotContain("did not connect for the scheduled call", AiReminderDelivery.Prompt(reminder));
    }

    [AiDatabaseFact]
    public async Task UnansweredCallsQueueOneReminderAndKeepTheirCallHistory()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("Database=bantera_ai_verify", connection);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
        var user = new User { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        var other = new User { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        UserPushToken Token(User owner, string platform) => new() {
            Id = Guid.NewGuid(), UserId = owner.Id, Platform = platform, Token = Guid.NewGuid().ToString(),
            SupportsCalls = platform == "ios-voip", CreatedAt = now, UpdatedAt = now, LastSeenAt = now
        };
        var voip = Token(user, "ios-voip");
        var alert = Token(user, "ios");
        var otherAlert = Token(other, "ios");
        db.AddRange(user, other, voip, alert, otherAlert);
        await db.SaveChangesAsync();
        try {
            var service = new AiCallbackService(db, Options.Create(new ApnsSettings {
                KeyId = "test", TeamId = "test", BundleId = "test", PrivateKeyPem = "test"
            }));
            var metadata = new AiClientMetadata(new("Pacific/Auckland", 780), voip.Token, AlertPushToken: alert.Token);
            var args = JsonSerializer.SerializeToElement(new { explicitCallRequested = true, delaySeconds = 60, reminder = "Bring your laptop" });
            async Task<Guid> Schedule(string key, string? alertToken) {
                var result = await service.ExecuteAsync(user.Id, metadata with { AlertPushToken = alertToken }, key, "schedule_callback", args, default);
                return JsonSerializer.SerializeToElement(result).GetProperty("id").GetGuid();
            }
            var ringing = await Schedule("ringing", alert.Token);
            var legacy = await Schedule("no alert", null);
            var foreign = await Schedule("foreign alert", otherAlert.Token);
            var answered = await Schedule("answered", alert.Token);
            var cancelled = await Schedule("cancelled", alert.Token);
            var stale = await Schedule("outside retention", alert.Token);
            var pending = await Schedule("within ring window", alert.Token);
            var timedOutOnOlderApp = await Schedule("legacy expiry DELETE", alert.Token);
            var calls = db.AiCallbacks.Where(c => c.UserId == user.Id && c.Delivery == "call");
            await calls.ExecuteUpdateAsync(s => s.SetProperty(c => c.DueAt, now.AddMinutes(-1)));
            await calls.Where(c => c.Id == ringing).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "ringing"));
            await calls.Where(c => c.Id == answered).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "answered"));
            await calls.Where(c => c.Id == cancelled).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, "ringing").SetProperty(c => c.DueAt, now.AddSeconds(-44)));
            Assert.Equal(1, await AiCallbackService.CancelFromDeviceAsync(db, user.Id, cancelled, now, default));
            // The published app receives a whole-second expiry and can time out
            // before the worker's precise timestamp has elapsed.
            var fractionalDue = now.AddSeconds(-44.75);
            await calls.Where(c => c.Id == timedOutOnOlderApp).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, "ringing").SetProperty(c => c.DueAt, fractionalDue));
            Assert.Equal(0, await AiCallbackService.CancelFromDeviceAsync(db, user.Id, timedOutOnOlderApp, now, default));
            var expiryCheckAt = now.AddMilliseconds(300);
            await calls.Where(c => c.Id == stale).ExecuteUpdateAsync(s => s.SetProperty(c => c.DueAt, now.AddDays(-8)));
            await calls.Where(c => c.Id == pending).ExecuteUpdateAsync(s => s.SetProperty(c => c.DueAt, now.AddSeconds(-44)));

            await using var competingWorker = new AppDbContext(options);
            await Task.WhenAll(AiCallbackWorker.ExpireCallsAsync(db, expiryCheckAt, default),
                AiCallbackWorker.ExpireCallsAsync(competingWorker, expiryCheckAt, default));
            await AiCallbackWorker.ExpireCallsAsync(db, expiryCheckAt, default);

            var messages = await db.AiCallbacks.AsNoTracking().Include(c => c.PushToken)
                .Where(c => c.UserId == user.Id && c.Delivery == "message").ToListAsync();
            Assert.Equal(4, messages.Count);
            AiCallback Message(Guid call) => Assert.Single(messages, m => m.RequestKey == AiReminderDelivery.MissedCallKeyPrefix + call);
            var voicemail = Message(ringing);
            Assert.Equal("queued", voicemail.Status);
            Assert.Equal("Bring your laptop", voicemail.Reminder);
            Assert.Equal("Pacific/Auckland", voicemail.TimeZone);
            Assert.Equal(voip.Id, voicemail.PushTokenId);
            Assert.Equal(alert.Id, (await AiReminderDelivery.AlertTokenAsync(db, voicemail, default))?.Id);
            Assert.Null(await AiReminderDelivery.AlertTokenAsync(db, Message(legacy), default));
            Assert.Null(await AiReminderDelivery.AlertTokenAsync(db, Message(foreign), default));
            var history = await calls.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Status);
            Assert.Equal("missed", history[ringing]);
            Assert.Equal("missed", history[timedOutOnOlderApp]);
            Assert.Equal("queued", Message(timedOutOnOlderApp).Status);
            Assert.Equal("missed", history[stale]);
            Assert.Equal("answered", history[answered]);
            Assert.Equal("cancelled", history[cancelled]);
            Assert.Equal("scheduled", history[pending]);

            await db.UserPushTokens.Where(t => t.Id == alert.Id).ExecuteDeleteAsync();
            Assert.Null(await AiReminderDelivery.AlertTokenAsync(db, voicemail, default));
            Assert.True(await db.AiCallbacks.AnyAsync(c => c.Id == voicemail.Id));
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.ChatNotificationsEnabled, false));
            await calls.Where(c => c.Id == pending).ExecuteUpdateAsync(s => s.SetProperty(c => c.DueAt, now.AddMinutes(-1)));
            await AiCallbackWorker.ExpireCallsAsync(db, expiryCheckAt, default);
            Assert.Equal(4, await db.AiCallbacks.CountAsync(c => c.UserId == user.Id && c.Delivery == "message"));
        }
        finally {
            await db.Users.Where(u => u.Id == user.Id || u.Id == other.Id).ExecuteDeleteAsync();
        }
    }
}

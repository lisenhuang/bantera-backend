using System.Text.Json;
using BanteraApi.Admin;
using BanteraApi.Chat.Ai;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public sealed class AiBugReportTests
{
    private static JsonElement Args(bool confirmed = true, string summary = "Audio pauses unexpectedly") => JsonSerializer.SerializeToElement(new {
        summary, details = "Playback stops when I scroll. I expected it to keep playing.", category = "audio", userConfirmed = confirmed, consentQuote = "Yes, send that report.",
        userId = Guid.Empty, source = "spoofed", createdAt = "2000-01-01"
    });
    [Fact]
    public void MissingOrInvalidConsentAndBoundsAreRejected()
    {
        Assert.Null(AiBugReportTool.Read(Args(false)));
        Assert.Null(AiBugReportTool.Read(Args(summary: new string('a', 161))));
        Assert.Null(AiBugReportTool.Read(JsonSerializer.SerializeToElement(new { summary = "Bug", details = "Broken", category = "audio", consentQuote = "yes" })));
        Assert.Null(AiBugReportTool.Read(JsonSerializer.SerializeToElement(new { summary = "Bug", details = "Broken", category = "audio", consentQuote = " ", userConfirmed = true })));
        Assert.Null(AiBugReportTool.Read(JsonSerializer.SerializeToElement(new { summary = "Bug", details = "Broken", category = "invalid", consentQuote = "yes", userConfirmed = true })));
        Assert.Null(AiBugReportTool.Read(JsonSerializer.SerializeToElement(new { summary = "Bug", details = "Broken", category = "audio", consentQuote = "yes", userConfirmed = "true" })));
    }
    [Theory]
    [InlineData("voice_message", false)]
    [InlineData("live_call", false)]
    [InlineData("callkit", true)]
    [InlineData("text_chat", false)]
    public void EveryConversationModeHasServerToolAndConsentPolicy(string source, bool phone)
    {
        var metadata = new AiClientMetadata(new("UTC", 0), null) { PhoneCallback = phone, ReportSource = source };
        var setup = GeminiLiveService.SessionSetup("gemini-3.8-live", new(), source is "voice_message" or "text_chat", [], metadata, "Puck", null, "test", null);
        var json = setup.ToJsonString();
        Assert.Contains("submit_bug_report", json);
        Assert.Contains("Wait for a clear affirmative reply", json);
        Assert.Contains("reportId", json);
        Assert.Equal(source, AiBugReportTool.Source(metadata));
        Assert.Contains(AiBugReportTool.Name, AiCallbackService.ToolNames);
        Assert.Equal("voice_message", AiClientMetadata.Read("{\"reportSource\":\"callkit\",\"phoneCallback\":true}").ReportSource);
        Assert.False(AiClientMetadata.Read("{\"phoneCallback\":true}").PhoneCallback);
    }
    [RankingDatabaseFact]
    public async Task StoresOnlyConfirmedReportsWithTrustedIdentityAndTimeAndDeduplicatesRetries()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_RANKING_TEST_DB")!;
        Assert.Contains("bantera_word_rankings_verify", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE users ("Id" uuid PRIMARY KEY, "Name" text, "Status" text, "DeletedAt" timestamptz);
            CREATE TEMP TABLE ai_bug_reports ("Id" uuid PRIMARY KEY, "UserId" uuid REFERENCES users("Id"),
              "Summary" varchar(160) NOT NULL, "Details" varchar(4000) NOT NULL, "Category" varchar(32) NOT NULL,
              "Source" varchar(32) NOT NULL, "ConsentQuote" varchar(500) NOT NULL, "DedupeKey" varchar(64) NOT NULL,
              "CreatedAt" timestamptz NOT NULL, UNIQUE ("UserId", "DedupeKey"));
            """);
        var user = Guid.NewGuid(); var other = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users VALUES ({user}, 'Test learner', 'active', NULL), ({other}, 'Other learner', 'active', NULL)");
        var service = new AiCallbackService(db, Options.Create(new BanteraApi.Chat.ApnsSettings()));
        var metadata = new AiClientMetadata(new("UTC", 0), null) { PhoneCallback = true };
        async Task<JsonElement> Submit(Guid id, bool confirmed) => JsonSerializer.SerializeToElement(await service.ExecuteAsync(id, metadata, "test-request", AiBugReportTool.Name, Args(confirmed), default));
        Assert.False((await Submit(user, false)).GetProperty("submitted").GetBoolean());
        Assert.Equal(0, await db.AiBugReports.CountAsync());
        var before = DateTime.UtcNow;
        var receipt = await Submit(user, true);
        Assert.True(receipt.GetProperty("submitted").GetBoolean());
        Assert.Equal(receipt.GetProperty("reportId").GetGuid(), (await Submit(user, true)).GetProperty("reportId").GetGuid());
        Assert.Equal(1, await db.AiBugReports.CountAsync());
        var report = await db.AiBugReports.AsNoTracking().SingleAsync();
        Assert.Equal(user, report.UserId);
        Assert.Equal("callkit", report.Source);
        Assert.InRange(report.CreatedAt, before, DateTime.UtcNow);
        Assert.Equal("Yes, send that report.", report.ConsentQuote);
        Assert.True((await Submit(other, true)).GetProperty("submitted").GetBoolean());
        var first = await AdminBugReports.ListAsync(db, 1, 0, default);
        var second = await AdminBugReports.ListAsync(db, 1, 1, default);
        Assert.Equal(2, first.Total); Assert.Single(first.Items); Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        Assert.Equal("Other learner", first.Items[0].UserName);
        Assert.Empty((await AdminBugReports.ListAsync(db, 20, 100, default)).Items);
        // A failed persistence operation must throw, never produce a success receipt.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE ai_bug_reports");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => service.ExecuteAsync(user, metadata, "failure", AiBugReportTool.Name, Args(), default));
    }
}

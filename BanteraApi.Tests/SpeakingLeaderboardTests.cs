using BanteraApi.Database;
using BanteraApi.Leaderboards;
using BanteraApi.Profile;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Security.Claims;
using Xunit;

namespace BanteraApi.Tests;
public class SpeakingLeaderboardTests
{
    [Theory]
    [InlineData("2026-10-21T00:00:00Z", "2026-10-15", "2026-10-21")]
    [InlineData("2026-10-19T23:59:59Z", "2026-10-13", "2026-10-19")]
    [InlineData("2026-01-01T00:00:00Z", "2025-12-26", "2026-01-01")]
    public void RollingCalendarWindowIsNotMondayWeek(string now, string from, string through)
    {
        var window = SpeakingLeaderboardService.Window(DateTime.Parse(now).ToUniversalTime());
        Assert.Equal(DateOnly.Parse(from), window.From);
        Assert.Equal(DateOnly.Parse(through), window.Through);
    }
    [Theory]
    [InlineData("2026-10-22T00:00:00Z", "2026-09-23")]
    [InlineData("2026-01-01T00:00:00Z", "2025-12-03")]
    [InlineData("2024-03-01T23:59:59Z", "2024-02-01")]
    public void ThirtyDayWindowIncludesTodayAndPreviousTwentyNineDays(string now, string from)
    {
        var window = SpeakingLeaderboardService.Window(DateTime.Parse(now).ToUniversalTime(), 30);
        Assert.Equal(DateOnly.Parse(from), window.From);
        Assert.Equal(29, window.Through.DayNumber - window.From.DayNumber);
    }

    [Theory]
    [InlineData("2026-10-27T00:00:00Z", "2026-10-27")]
    [InlineData("2026-10-27T23:59:59Z", "2026-10-27")]
    public void TodayUsesOneUtcCalendarDate(string now, string today)
    {
        var window = SpeakingLeaderboardService.Window(DateTime.Parse(now).ToUniversalTime(), 1);
        Assert.Equal(DateOnly.Parse(today), window.From);
        Assert.Equal(window.From, window.Through);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void InvalidWindowIsRejected(int days) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeakingLeaderboardService.Window(DateTime.UtcNow, days));

    [Fact]
    public void SnapshotValidationRejectsDuplicateLanguageAliasesAndInvalidCounts()
    {
        var today = new DateOnly(2026, 10, 21); var id = Guid.NewGuid();
        Assert.True(SpeakingLeaderboardEndpoints.Valid(new(id, [new(today, "en-US", 0, 8000), new(today, "ja", 0, 4450)], null), today));
        Assert.False(SpeakingLeaderboardEndpoints.Valid(new(id, [new(today, "en-US", 0, 10), new(today, "en-GB", 0, 20)], null), today));
        Assert.False(SpeakingLeaderboardEndpoints.Valid(new(id, [new(today, "en", 0, -1)], null), today));
        Assert.False(SpeakingLeaderboardEndpoints.Valid(new(Guid.Empty, [], null), today));
    }
    [RankingDatabaseFact]
    public async Task PostgresRankingPreservesAllLanguagesTiesVisibleProfilesOwnPositionAndUtcReplacement()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_RANKING_TEST_DB")!;
        Assert.Contains("bantera_word_rankings_verify", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE users ("Id" uuid PRIMARY KEY, "Name" text, "Role" text, "Status" text, "DeletedAt" timestamptz, "AvatarObjectKey" text, "AvatarUpdatedAt" timestamptz, "LearningLanguage" text);
            CREATE TEMP TABLE user_word_activity ("UserId" uuid, "DeviceId" uuid, "Date" date, "ListenedWords" bigint, "SpokenWords" bigint);
            CREATE TEMP TABLE user_language_word_activity (LIKE user_word_activity); ALTER TABLE user_language_word_activity ADD COLUMN "Language" text;
            CREATE TEMP TABLE speaking_visibility ("UserId" uuid PRIMARY KEY, "ShowProfile" boolean);
            CREATE TEMP TABLE speaking_devices ("UserId" uuid, "DeviceId" uuid, "LegacyThroughDate" date, PRIMARY KEY ("UserId", "DeviceId"));
            CREATE TEMP TABLE speaking_day_windows ("UserId" uuid, "DeviceId" uuid, "Date" date, "Language" text, "FromUtc" timestamptz, "ThroughUtc" timestamptz, "SpokenWords" bigint, PRIMARY KEY ("UserId","DeviceId","Date","Language"));
            CREATE TEMP TABLE speaking_events ("UserId" uuid, "DeviceId" uuid, "EventId" uuid, "OccurredAt" timestamptz, "Language" text, "SpokenWords" bigint);
            CREATE TEMP TABLE speaking_days ("UserId" uuid, "DeviceId" uuid, "Date" date, "Language" text, "SpokenWords" bigint);
            INSERT INTO users SELECT ('00000000-0000-0000-0000-' || lpad(i::text,12,'0'))::uuid, 'Learner '||i, 'user', 'active', null, null, null, 'ja' FROM generate_series(1,105) i;
            INSERT INTO user_language_word_activity ("UserId", "DeviceId", "Date", "Language", "ListenedWords", "SpokenWords")
            SELECT "Id", "Id", '2026-10-21', 'en', 0, 1000 FROM users;
            UPDATE user_language_word_activity SET "SpokenWords" = 8000 WHERE "UserId" = '00000000-0000-0000-0000-000000000001';
            INSERT INTO user_language_word_activity VALUES ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000001','2026-10-15',0,4450,'ja');
            UPDATE user_language_word_activity SET "SpokenWords" = 12450 WHERE "UserId" = '00000000-0000-0000-0000-000000000002';
            UPDATE user_language_word_activity SET "SpokenWords" = 12000 WHERE "UserId" = '00000000-0000-0000-0000-000000000003';
            INSERT INTO user_word_activity VALUES ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000001','2026-10-14',0,999999),('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000001','2026-10-22',0,999999);
            """);
        var service = new SpeakingLeaderboardService(db, null!, Microsoft.Extensions.Options.Options.Create(new BanteraApi.Auth.JwtSettings { Secret = "isolated-ranking-test-secret-with-sufficient-length" }));
        var now = new DateTime(2026,10,21,12,0,0,DateTimeKind.Utc);
        var me = Guid.Parse("00000000-0000-0000-0000-000000000105");
        var report = await service.GetAsync(me, new DefaultHttpContext(), default, now);
        Assert.False(report.CanChoosePeriod);
        Assert.All(report.Rows, row => Assert.Null(row.AdminUserId));
        Assert.DoesNotContain("AdminUserId", JsonSerializer.Serialize(report));
        var adminContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "test"))
        };
        var today = await service.GetAsync(me, adminContext, default, now, days: 1);
        Assert.True(today.CanChoosePeriod);
        Assert.Equal(today.FromDate, today.ThroughDate);
        Assert.Equal(12450, today.Rows[0].SpokenWords);
        Assert.Equal(8000, today.Rows.Single(row => row.Name == "Learner 1").SpokenWords);
        Assert.All(today.Rows, row => Assert.NotNull(row.AdminUserId));
        Assert.Equal(me, today.You.AdminUserId);
        Assert.Equal(100, report.Rows.Length); Assert.Equal(105, report.Participants);
        Assert.True(report.HasMore);
        var next = await service.GetAsync(me, new DefaultHttpContext(), default, now, offset: 100);
        Assert.Equal(5, next.Rows.Length); Assert.False(next.HasMore);
        Assert.True(next.Rows.Last().IsMe);
        Assert.Equal(report.You.Key, next.Rows.Last().Key);
        Assert.Empty(report.Rows.Select(r => r.Key).Intersect(next.Rows.Select(r => r.Key)));
        Assert.Equal(new long?[] {1,1,3}, report.Rows.Take(3).Select(r => r.Rank));
        Assert.Equal(12450, report.Rows[0].SpokenWords);
        Assert.Equal("ja", report.Rows[0].LearningLanguage);
        Assert.DoesNotContain("Languages", JsonSerializer.Serialize(report));
        Assert.True(report.You.IsMe); Assert.Equal(4, report.You.Rank); Assert.DoesNotContain(report.Rows, r => r.IsMe);
        Assert.True(report.ShowProfile);
        Assert.All(report.Rows, r => { Assert.False(r.ProfileHidden); Assert.NotNull(r.Name); Assert.Null(r.AvatarUrl); });
        Assert.DoesNotContain("00000000-", JsonSerializer.Serialize(report));
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO speaking_visibility VALUES ('00000000-0000-0000-0000-000000000001', true);
            UPDATE users SET "LearningLanguage" = 'fr' WHERE "Id" = '00000000-0000-0000-0000-000000000001';
            """);
        report = await service.GetAsync(me, new DefaultHttpContext(), default, now);
        Assert.Equal("Learner 1", report.Rows[0].Name); Assert.Equal(12450, report.Rows[0].SpokenWords);
        Assert.Equal("fr", report.Rows[0].LearningLanguage);
        await db.Database.ExecuteSqlRawAsync("UPDATE speaking_visibility SET \"ShowProfile\" = false");
        report = await service.GetAsync(me, new DefaultHttpContext(), default, now);
        Assert.Equal("Learner 1", report.Rows[0].Name); Assert.Equal(12450, report.Rows[0].SpokenWords);
        Assert.False(report.Rows[0].ProfileHidden);
        Assert.Equal("fr", report.Rows[0].LearningLanguage);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO user_word_activity VALUES
            ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000001','2026-09-22',0,7),
            ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000001','2026-09-21',0,999999);
            """);
        var monthly = await service.GetAsync(me, new DefaultHttpContext(), default, now, days: 30);
        Assert.Equal(new DateOnly(2026, 9, 22), monthly.FromDate);
        Assert.Equal(12450 + 999999 + 7, monthly.Rows[0].SpokenWords);
        Assert.Equal(1, monthly.Rows[0].Rank);
        var legacy = await service.GetAsync(me, new DefaultHttpContext(), default, now);
        Assert.Equal(12450, legacy.Rows[0].SpokenWords);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO speaking_devices VALUES ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000001',null);
            INSERT INTO speaking_days VALUES ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000001','2026-10-21','ja',100);
            """);
        report = await service.GetAsync(Guid.Parse("00000000-0000-0000-0000-000000000001"), new DefaultHttpContext(), default, now);
        Assert.True(report.ShowProfile); Assert.False(report.You.ProfileHidden);
        Assert.Equal(100, report.You.SpokenWords); Assert.Equal(105, report.You.Rank);
        var empty = await service.GetAsync(me, new DefaultHttpContext(), default, now.AddDays(20));
        Assert.Empty(empty.Rows); Assert.Null(empty.You.Rank); Assert.Equal(0, empty.You.SpokenWords);
    }
}

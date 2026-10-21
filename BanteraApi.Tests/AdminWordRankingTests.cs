using BanteraApi.Admin;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BanteraApi.Tests;

public sealed class AdminWordRankingTests
{
    [Theory]
    [InlineData("2026-10-18T11:30:00Z", "2026-10-19", "2026-10-19")]
    [InlineData("2026-10-18T10:30:00Z", "2026-10-18", "2026-10-12")]
    [InlineData("2026-04-05T12:30:00Z", "2026-04-06", "2026-04-06")]
    public void ReportingDatesUseAucklandAndMondayWeeks(string utc, string date, string monday)
    {
        var today = AdminWordRankingService.ReportingDate(DateTime.Parse(utc).ToUniversalTime());
        Assert.Equal(DateOnly.Parse(date), today);
        Assert.Equal(today, AdminWordRankingService.StartDate("today", today));
        Assert.Equal(DateOnly.Parse(monday), AdminWordRankingService.StartDate("week", today));
        Assert.Null(AdminWordRankingService.StartDate("total", today));
    }

    [RankingDatabaseFact]
    public async Task PostgresCombinesBucketsRanksTiesAndFiltersDatesAndAccounts()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_RANKING_TEST_DB")!;
        Assert.Contains("bantera_word_rankings_verify", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.OpenConnectionAsync();
        // Temporary tables isolate the fixture from other tests and any existing data.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE users ("Id" uuid PRIMARY KEY, "Name" text, "Role" text, "DeletedAt" timestamptz);
            CREATE TEMP TABLE user_word_activity ("UserId" uuid, "DeviceId" uuid, "Date" date, "ListenedWords" bigint, "SpokenWords" bigint);
            CREATE TEMP TABLE user_language_word_activity (LIKE user_word_activity);
            INSERT INTO users VALUES
                ('00000000-0000-0000-0000-000000000001', 'One', 'user', null),
                ('00000000-0000-0000-0000-000000000002', 'Two', 'user', null),
                ('00000000-0000-0000-0000-000000000003', 'Three', 'user', null),
                ('00000000-0000-0000-0000-000000000004', 'System', 'system', null),
                ('00000000-0000-0000-0000-000000000005', 'Deleted', 'user', now());
            INSERT INTO user_word_activity ("UserId", "Date", "ListenedWords", "SpokenWords") VALUES
                ('00000000-0000-0000-0000-000000000001', '2026-10-21', 40, 10),
                ('00000000-0000-0000-0000-000000000001', '2026-10-21', 10, 5),
                ('00000000-0000-0000-0000-000000000002', '2026-10-21', 100, 30),
                ('00000000-0000-0000-0000-000000000003', '2026-10-21', 0, 100),
                ('00000000-0000-0000-0000-000000000004', '2026-10-21', 9999, 9999),
                ('00000000-0000-0000-0000-000000000005', '2026-10-21', 9999, 9999),
                ('00000000-0000-0000-0000-000000000003', '2026-10-19', 200, 0),
                ('00000000-0000-0000-0000-000000000003', '2026-10-18', 300, 0),
                ('00000000-0000-0000-0000-000000000003', '2026-10-22', 400, 0);
            INSERT INTO user_language_word_activity ("UserId", "Date", "ListenedWords", "SpokenWords") VALUES
                ('00000000-0000-0000-0000-000000000001', '2026-10-21', 20, 10),
                ('00000000-0000-0000-0000-000000000001', '2026-10-21', 30, 25);
            """);
        var service = new AdminWordRankingService(db);
        var now = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
        var today = await service.GetAsync("today", "listened", 1, utcNow: now);
        Assert.True(today.HasMore);
        Assert.Equal(100, today.Users[0].ListenedWords);
        Assert.Equal(50, today.Users[0].SpokenWords);
        Assert.Equal(1, today.Users[0].Rank);
        var second = await service.GetAsync("today", "listened", 1, 1, utcNow: now);
        Assert.False(second.HasMore);
        Assert.Equal(1, second.Users[0].Rank);
        Assert.NotEqual(today.Users[0].UserId, second.Users[0].UserId);
        var spoken = await service.GetAsync("today", "spoken", utcNow: now);
        Assert.Equal(new long[] { 100, 50, 30 }, spoken.Users.Select(x => x.SpokenWords));
        var week = await service.GetAsync("week", "listened", utcNow: now);
        Assert.Equal(new long[] { 200, 100, 100 }, week.Users.Select(x => x.ListenedWords));
        Assert.Equal(new long[] { 1, 2, 2 }, week.Users.Select(x => x.Rank));
        var total = await service.GetAsync("total", "listened", utcNow: now);
        Assert.Equal(900, total.Users[0].ListenedWords);
        Assert.Equal(3, total.Users.Count);
        Assert.Empty((await service.GetAsync("today", "listened", 20, 100, utcNow: now)).Users);
        Assert.Empty((await service.GetAsync("today", "spoken", utcNow: now.AddDays(10))).Users);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync("bad", "spoken"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync("today", "bad"));
    }
}

public sealed class RankingDatabaseFactAttribute : FactAttribute
{
    public RankingDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BANTERA_RANKING_TEST_DB")))
            Skip = "Set BANTERA_RANKING_TEST_DB to an isolated bantera_word_rankings_verify PostgreSQL database.";
    }
}

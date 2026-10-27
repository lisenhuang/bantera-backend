using BanteraApi.Auth;
using BanteraApi.Database;
using BanteraApi.Leaderboards;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class SpeakingRollingWindowTests
{
    [Fact]
    public void EventValidationRejectsFutureStaleAndDuplicateEvents()
    {
        var now = DateTimeOffset.Parse("2026-10-27T02:15:30Z");
        var item = new SpeakingEventInput(Guid.NewGuid(), now, "en-NZ", 51);
        var device = Guid.NewGuid();
        Assert.True(SpeakingEventEndpoints.Valid(new(device, [item]), now));
        Assert.False(SpeakingEventEndpoints.Valid(new(device, [item, item]), now));
        Assert.False(SpeakingEventEndpoints.Valid(new(device, [item with { OccurredAt = now.AddMinutes(6) }]), now));
        Assert.False(SpeakingEventEndpoints.Valid(new(device, [item with { OccurredAt = now.AddDays(-36) }]), now));
        Assert.False(SpeakingEventEndpoints.Valid(new(device, [item with { SpokenWords = -1 }]), now));
        Assert.False(SpeakingEventEndpoints.Valid(new(Guid.Empty, [item]), now));
    }

    [RankingDatabaseFact]
    public async Task RollingPeriodsUseExactBoundariesAndDoNotCountRetriesOrDailyTotalsTwice()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_RANKING_TEST_DB")!;
        Assert.Contains("bantera_word_rankings_verify", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE users ("Id" uuid PRIMARY KEY, "Name" text, "Role" text, "Status" text, "DeletedAt" timestamptz, "AvatarObjectKey" text, "AvatarUpdatedAt" timestamptz, "LearningLanguage" text);
            CREATE TEMP TABLE user_word_activity ("UserId" uuid, "DeviceId" uuid, "Date" date, "SpokenWords" bigint);
            CREATE TEMP TABLE user_language_word_activity (LIKE user_word_activity); ALTER TABLE user_language_word_activity ADD COLUMN "Language" text;
            CREATE TEMP TABLE speaking_devices ("UserId" uuid, "DeviceId" uuid, "LegacyThroughDate" date);
            CREATE TEMP TABLE speaking_days ("UserId" uuid, "DeviceId" uuid, "Date" date, "Language" text, "SpokenWords" bigint);
            CREATE TEMP TABLE speaking_day_windows ("UserId" uuid, "DeviceId" uuid, "Date" date, "Language" text, "FromUtc" timestamptz, "ThroughUtc" timestamptz, "SpokenWords" bigint, PRIMARY KEY ("UserId","DeviceId","Date","Language"));
            CREATE TEMP TABLE speaking_events ("UserId" uuid, "DeviceId" uuid, "EventId" uuid, "OccurredAt" timestamptz, "Language" text, "SpokenWords" bigint, PRIMARY KEY ("UserId", "DeviceId", "EventId"));
            """);
        var owner = Guid.NewGuid(); var device = Guid.NewGuid();
        var now = new DateTime(2026,10,27,2,15,30,DateTimeKind.Utc);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users VALUES ({owner}, 'Learner', 'user', 'active', null, null, null, 'en-NZ')
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO speaking_devices VALUES ({owner}, {device}, null)
            """);
        var service = new SpeakingLeaderboardService(db, null!, Options.Create(new JwtSettings { Secret = "isolated-rolling-test-secret-long-enough" }));
        foreach (var hours in new[] {24, 168, 720})
        {
            await db.Database.ExecuteSqlRawAsync("TRUNCATE speaking_events, speaking_days");
            var since = now.AddHours(-hours);
            var events = new[] {
                new SpeakingEventInput(Guid.NewGuid(), since, "en", 1000),
                new SpeakingEventInput(Guid.NewGuid(), since.AddSeconds(1), "en", 10),
                new SpeakingEventInput(Guid.NewGuid(), now.AddSeconds(-1), "en", 20),
                new SpeakingEventInput(Guid.NewGuid(), now, "en", 30),
                new SpeakingEventInput(Guid.NewGuid(), now.AddSeconds(1), "en", 2000),
            };
            var batch = new SpeakingEventBatch(device, events);
            await SpeakingEventEndpoints.SaveAsync(db, owner, batch, now, default);
            await SpeakingEventEndpoints.SaveAsync(db, owner, batch, now, default);
            var report = await service.GetAsync(owner, new DefaultHttpContext(), default, now, rollingHours: hours);
            Assert.Equal(60, report.You.SpokenWords);
            Assert.Equal(since, report.FromUtc); Assert.Equal(now, report.ThroughUtc);
            // The first partial day cannot be guessed. Current UTC totals are
            // known to be within the window, after subtracting timestamped events.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO speaking_days VALUES ({owner}, {device}, {DateOnly.FromDateTime(since)}, 'en', 99999),
                    ({owner}, {device}, {DateOnly.FromDateTime(now)}, 'en', 2090)
                """);
            report = await service.GetAsync(owner, new DefaultHttpContext(), default, now, rollingHours: hours);
            Assert.Equal(100, report.You.SpokenWords); // 60 exact + 40 current-day untimed
            if (hours > 24)
            {
                var interior = now.AddDays(-2);
                await SpeakingEventEndpoints.SaveAsync(db, owner, new(device, [new(Guid.NewGuid(), interior, "en", 40)]), now, default);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO speaking_days VALUES ({owner}, {device}, {DateOnly.FromDateTime(interior)}, 'en', 100)
                    """);
                report = await service.GetAsync(owner, new DefaultHttpContext(), default, now, rollingHours: hours);
                Assert.Equal(200, report.You.SpokenWords); // 100 edges + 40 timestamped + 60 legacy
            }
        }
        await db.Database.ExecuteSqlRawAsync("TRUNCATE speaking_events, speaking_days");
        var other = Guid.NewGuid(); var otherDevice = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE users SET "LearningLanguage"='mi-NZ' WHERE "Id"={owner};
            INSERT INTO users VALUES ({other}, 'Other learner', 'user', 'active', null, null, null, 'ko-KR');
            INSERT INTO speaking_devices VALUES ({other}, {otherDevice}, null);
            INSERT INTO speaking_days VALUES
                ({owner}, {device}, {DateOnly.FromDateTime(now)}, 'en', 9),
                ({owner}, {device}, {DateOnly.FromDateTime(now)}, 'ja', 11),
                ({owner}, {device}, {DateOnly.FromDateTime(now)}, 'yue', 47),
                ({other}, {otherDevice}, {DateOnly.FromDateTime(now)}, 'fr', 5);
            """);
        // Every participant's languages count, even with an unrelated profile language.
        var allLanguages = await service.GetAsync(owner, new DefaultHttpContext(), default, now, rollingHours: 24);
        Assert.Equal(67, allLanguages.You.SpokenWords); Assert.Equal(2, allLanguages.Participants);
        Assert.Equal(5, allLanguages.Rows.Single(r => !r.IsMe).SpokenWords);
        await SpeakingEventEndpoints.SaveAsync(db, owner, new(device, [new(Guid.NewGuid(), now.AddHours(-12), "es", 44)]), now, default);
        allLanguages = await service.GetAsync(owner, new DefaultHttpContext(), default, now, rollingHours: 24);
        Assert.Equal(111, allLanguages.You.SpokenWords);
        var localDate = DateOnly.FromDateTime(now);
        var midnightNz = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-13);
        var local = new SpeakingLocalDay(localDate, midnightNz, now,
            [new(localDate, "en", 0, 200), new(localDate, "ja", 0, 11), new(localDate, "yue", 0, 47)]);
        Assert.True(SpeakingLocalDayStorage.Valid(local, now));
        await SpeakingLocalDayStorage.SaveAsync(db, owner, device, local, default);
        await SpeakingLocalDayStorage.SaveAsync(db, owner, device, local, default);
        allLanguages = await service.GetAsync(owner, new DefaultHttpContext(), default, now, rollingHours: 24);
        Assert.Equal(302, allLanguages.You.SpokenWords); // 258 local + 44 Spanish
        // A timestamp inside the snapshot is already included, one outside adds.
        await SpeakingEventEndpoints.SaveAsync(db, owner, new(device,
            [new(Guid.NewGuid(), now.AddHours(-1), "en", 9), new(Guid.NewGuid(), midnightNz.AddHours(-1), "en", 20)]), now, default);
        allLanguages = await service.GetAsync(owner, new DefaultHttpContext(), default, now, rollingHours: 24);
        Assert.Equal(322, allLanguages.You.SpokenWords);
        // Same language on another device adds, rather than being swallowed by MAX.
        var secondDevice = Guid.NewGuid();
        await SpeakingEventEndpoints.SaveAsync(db, owner, new(secondDevice,
            [new(Guid.NewGuid(), now, "en", 5)]), now, default);
        allLanguages = await service.GetAsync(owner, new DefaultHttpContext(), default, now, rollingHours: 24);
        Assert.Equal(327, allLanguages.You.SpokenWords);
        // A later cutoff excludes a partial local-day interval; no guessed times.
        allLanguages = await service.GetAsync(owner, new DefaultHttpContext(), default, midnightNz.AddHours(25), rollingHours: 24);
        Assert.Equal(116, allLanguages.You.SpokenWords); // 44 Spanish + 14 English + 58 Japanese/Cantonese
        Assert.False(SpeakingLocalDayStorage.Valid(local with { ThroughUtc = now.AddMinutes(6) }, now));
        Assert.False(SpeakingLocalDayStorage.Valid(local with { FromUtc = now.AddDays(-2) }, now));

        // Production regression: all three users have UTC-device snapshots,
        // but two older clients only spoke before UTC midnight. Their local
        // October 27 totals are provably inside 24H even without a timezone.
        await db.Database.ExecuteSqlRawAsync("TRUNCATE users, speaking_days, speaking_devices, speaking_events, speaking_day_windows, user_language_word_activity");
        var missing = Guid.NewGuid(); var missingDevice = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users VALUES
              ({owner}, 'Ethan', 'admin', 'active', null, null, null, 'en-NZ'),
              ({other}, 'Yujia', 'user', 'active', null, null, null, 'en-US'),
              ({missing}, 'Maya', 'user', 'active', null, null, null, 'ja-JP');
            INSERT INTO speaking_devices VALUES ({owner},{device},null), ({other},{otherDevice},null), ({missing},{missingDevice},null);
            INSERT INTO speaking_days VALUES
              ({owner},{device},{localDate.AddDays(-1)},'en',191), ({owner},{device},{localDate},'en',9),
              ({owner},{device},{localDate},'ja',11), ({owner},{device},{localDate},'yue',47),
              ({other},{otherDevice},{localDate.AddDays(-1)},'en',384), ({missing},{missingDevice},{localDate.AddDays(-1)},'ja',6);
            INSERT INTO user_language_word_activity VALUES
              ({owner},{device},{localDate},200,'en'), ({owner},{device},{localDate},11,'ja'), ({owner},{device},{localDate},47,'yue'),
              ({other},{otherDevice},{localDate},384,'en'), ({missing},{missingDevice},{localDate},6,'ja');
            """);
        foreach (var hours in new[] {24,168,720}) {
            var restored = await service.GetAsync(owner, new DefaultHttpContext(), default, now, rollingHours:hours);
            Assert.Equal(3, restored.Participants);
            Assert.Equal(new long[] {384,258,6}, restored.Rows.Select(r=>r.SpokenWords));
            Assert.Equal(2, restored.You.Rank);
        }
        await SpeakingLocalDayStorage.SaveAsync(db, owner, device, local, default);
        var withNewClient = await service.GetAsync(owner, new DefaultHttpContext(), default, now, rollingHours:24);
        Assert.Equal(new long[] {384,258,6}, withNewClient.Rows.Select(r=>r.SpokenWords));
        // No false precision once the earliest possible time falls out of 24H.
        var later = await service.GetAsync(owner, new DefaultHttpContext(), default, now.AddHours(14), rollingHours:24);
        Assert.Equal(1, later.Participants);
        Assert.Equal(67, later.You.SpokenWords);

        var oldestClient = Guid.NewGuid(); var oldestDevice = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users VALUES ({oldestClient}, 'Older app', 'user', 'active', null, null, null, 'en-US');
            INSERT INTO user_language_word_activity VALUES
              ({oldestClient},{oldestDevice},{localDate},5,'en'),
              ({oldestClient},{oldestDevice},{localDate.AddDays(-1)},10,'en'),
              ({oldestClient},{oldestDevice},{localDate.AddDays(-2)},20,'en');
            """);
        await SpeakingEventEndpoints.SaveAsync(db, oldestClient, new(oldestDevice,
            [new(Guid.NewGuid(), now.AddHours(-1), "en", 3), new(Guid.NewGuid(), now.AddDays(-6), "en", 2)]), now, default);
        foreach (var hours in new[] {168,720}) {
            var legacy = await service.GetAsync(oldestClient, new DefaultHttpContext(), default, now, rollingHours:hours);
            Assert.Equal(37, legacy.You.SpokenWords); // 35 across local dates + 2 outside them; inside 3 not duplicated.
        }

    }
}

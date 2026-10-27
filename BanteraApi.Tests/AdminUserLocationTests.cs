using BanteraApi.Admin;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace BanteraApi.Tests;

public class AdminUserLocationTests
{
    [RankingDatabaseFact]
    public async Task LatestKnownLocationIsUserScopedAndDoesNotMixOlderCityIntoNewCountry()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_RANKING_TEST_DB")!;
        Assert.Contains("bantera_word_rankings_verify", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE user_activity_daily (
              "UserId" uuid NOT NULL, "Date" date NOT NULL,
              "CountryCode" text NULL, "City" text NULL);
            """);
        var user = Guid.NewGuid(); var other = Guid.NewGuid();
        Assert.Null(await AdminService.GetLastLocationAsync(db, user));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_activity_daily VALUES
              ({user}, '2026-10-25', 'NZ', 'Auckland'),
              ({user}, '2026-10-26', 'JP', NULL),
              ({user}, '2026-10-27', NULL, NULL),
              ({other}, '2026-10-27', 'CH', 'Zürich');
            """);
        var location = await AdminService.GetLastLocationAsync(db, user);
        Assert.Equal(new AdminUserLocation("JP", null, new DateOnly(2026, 10, 26)), location);
        Assert.Equal("Zürich", (await AdminService.GetLastLocationAsync(db, other))!.City);
    }

    [RankingDatabaseFact]
    public async Task UserListIncludesLatestCountryAndCatalogLanguageFlags()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_RANKING_TEST_DB")!;
        Assert.Contains("bantera_word_rankings_verify", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE users (
              "Id" uuid PRIMARY KEY, "Name" text, "Role" text, "Status" text,
              "NativeLanguage" text, "LearningLanguage" text, "CreatedAt" timestamptz,
              "LastLoginAt" timestamptz, "DeletedAt" timestamptz,
              "AvatarObjectKey" text, "AvatarUpdatedAt" timestamptz);
            CREATE TEMP TABLE user_identities ("UserId" uuid, "Provider" text, "ProviderEmail" text);
            CREATE TEMP TABLE user_videos ("UserId" uuid);
            CREATE TEMP TABLE user_activity_daily ("UserId" uuid, "Date" date, "CountryCode" text);
            """);
        var user = Guid.NewGuid(); var other = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users ("Id", "Name", "Role", "Status", "NativeLanguage", "LearningLanguage", "CreatedAt")
              VALUES ({user}, 'Test learner', 'user', 'active', 'zh-HK', 'en-NZ', now()),
                     ({other}, 'Other learner', 'user', 'active', 'ja', 'ko', now());
            INSERT INTO user_activity_daily VALUES
              ({user}, '2026-10-25', 'NZ'), ({user}, '2026-10-26', 'JP'),
              ({user}, '2026-10-27', NULL), ({other}, '2026-10-27', 'KR');
            """);
        var service = new AdminService(db, null!, null!, new Microsoft.AspNetCore.Http.HttpContextAccessor());
        var result = await service.ListUsersAsync(null, null, null, 20, 0);
        Assert.Equal(2, result.Total);
        var row = Assert.Single(result.Items, row => row.Id == user);
        Assert.Equal("JP", row.CountryCode);
        Assert.Equal("🇭🇰", row.NativeLanguageFlag);
        Assert.Equal("🇳🇿", row.LearningLanguageFlag);
        Assert.Equal("KR", Assert.Single(result.Items, row => row.Id == other).CountryCode);
    }

    [RankingDatabaseFact]
    public async Task CountryOnlyVisitNeverRetainsAnEarlierCityAndMissingHeadersKeepKnownLocation()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_RANKING_TEST_DB")!;
        Assert.Contains("bantera_word_rankings_verify", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE user_activity_daily (
              "UserId" uuid, "Date" date, "FirstSeenAt" timestamptz, "LastSeenAt" timestamptz,
              "TouchCount" integer, "MessagesSent" integer, "Source" text,
              "CountryCode" text, "Region" text, "City" text, PRIMARY KEY ("UserId", "Date"));
            """);
        var user = Guid.NewGuid();
        async Task Visit(BanteraApi.Activity.VisitorGeo geo) {
            using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
            var recorder = new BanteraApi.Activity.UserActivityRecorder(cache, Microsoft.Extensions.Logging.Abstractions.NullLogger<BanteraApi.Activity.UserActivityRecorder>.Instance);
            await recorder.TouchAsync(user, db, geo);
        }
        await Visit(new("NZ", "Auckland", "Auckland"));
        Assert.Equal("Auckland", (await AdminService.GetLastLocationAsync(db, user))!.City);
        await Visit(new("JP", null, null));
        var moved = await AdminService.GetLastLocationAsync(db, user);
        Assert.Equal("JP", moved!.CountryCode);
        Assert.Null(moved.City);
        await Visit(BanteraApi.Activity.VisitorGeo.None);
        Assert.Equal(moved, await AdminService.GetLastLocationAsync(db, user));
    }

    [Fact]
    public void LocationContractContainsOnlyCountryCityAndDate()
    {
        var json = JsonSerializer.SerializeToElement(new AdminUserLocation("NZ", "Auckland", new DateOnly(2026, 10, 27)), JsonSerializerOptions.Web);
        Assert.Equal(new[] { "countryCode", "city", "activityDate" }, json.EnumerateObject().Select(p => p.Name));
    }
}

using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Videos;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BanteraApi.Tests;

public class AudioLanguageGroupFilterTests
{
    [Theory]
    [InlineData("yue", "zh-HK,yue-CN,yue-Hant-HK")]
    [InlineData("zh-cn", "zh,zh-CN,zh-Hans-CN")]
    [InlineData("zh-tw", "zh-TW,zh-Hant-TW")]
    [InlineData("en", "en,en-NZ,en-US")]
    [InlineData(" FR ", "fr-FR,fr-CA")]
    public void GroupsAccentsWithoutMixingTheChineseGroups(string group, string expected)
    {
        var codes = new[] { "zh-HK", "yue-CN", "yue-Hant-HK", "zh", "zh-CN", "zh-Hans-CN",
            "zh-TW", "zh-Hant-TW", "en", "en-NZ", "en-US", "fr-FR", "fr-CA" };
        var query = codes.Select(code => new UserVideo { TranscriptLanguageCode = code }).AsQueryable();
        Assert.Equal(expected.Split(','), AudioLanguageGroupFilter.Apply(query, group).Select(v => v.TranscriptLanguageCode));
    }

    [Fact]
    public void GroupFilteringRunsBeforePaginationAndTranslatesToPostgres()
    {
        var query = new[] { "en-NZ", "yue-CN", "zh-CN", "zh-HK" }
            .Select(code => new UserVideo { TranscriptLanguageCode = code }).AsQueryable();
        Assert.Equal("zh-HK", AudioLanguageGroupFilter.Apply(query, "yue").Skip(1).Take(1).Single().TranscriptLanguageCode);
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused_language_group_test").Options);
        foreach (var group in new[] { "yue", "zh-cn", "zh-tw", "en" })
            Assert.Contains("WHERE", AudioLanguageGroupFilter.Apply(db.UserVideos, group).ToQueryString());
    }
}

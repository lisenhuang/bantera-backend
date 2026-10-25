using System.Text.Json;
using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;

public class PracticeNumericMatchingTests
{
    public record Fixture(string Name, string Transcript, string[] Choices, int? Expected,
        string Locale, string[] UncertainWords)
    {
        public override string ToString() => Name;
    }

    private static Fixture[] ReadCases() => JsonSerializer.Deserialize<Fixture[]>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "practice_numeric_matching.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    public static IEnumerable<object[]> Cases() => ReadCases().Select(row => new object[] { row });

    [Theory, MemberData(nameof(Cases))]
    public void NumericFormattingDoesNotChangeTheSelectedReply(Fixture row) => Assert.Equal(row.Expected,
        PracticeAnswerMatcher.Match(row.Transcript, row.Choices, row.Locale, row.UncertainWords));

    [Fact]
    public void EveryLearningLocaleHasEquivalentAndDifferentNumberCoverage()
    {
        var rows = ReadCases();
        foreach (var language in LearningLanguageCatalog.Items)
        {
            Assert.Contains(rows, row => row.Locale == language.Identifier &&
                row.Name.EndsWith("cardinal accepts digit spelling", StringComparison.Ordinal) && row.Expected == 0);
            Assert.Contains(rows, row => row.Locale == language.Identifier &&
                row.Name.EndsWith("different cardinal remains unclear", StringComparison.Ordinal) && row.Expected is null);
        }
    }
}

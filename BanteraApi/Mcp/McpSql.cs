namespace BanteraApi.Mcp;

/// <summary>
/// Row shapes for raw SQL queries run through <c>db.Database.SqlQuery&lt;T&gt;</c>.
///
/// EF wraps the SQL in a subquery, so: no trailing semicolon, and every column must be
/// aliased with a quoted name matching the property. COUNT() is bigint, hence long.
/// </summary>
public static class McpSql
{
    public sealed record BucketCountRow(DateTime Bucket, long Count);
    public sealed record BucketPairRow(DateTime Bucket, long CountA, long CountB);
    public sealed record ContentBucketRow(DateTime Bucket, long Uploads, long AiAudio, long StorageBytes);
    public sealed record ProviderBucketRow(DateTime Bucket, string Provider, long Count);
    public sealed record ActiveRow(DateOnly Date, long Dau, long Wau, long Mau);
    public sealed record CohortRow(DateOnly CohortWeek, int WeekOffset, long Retained);
    public sealed record CohortSizeRow(DateOnly CohortWeek, long Size);
    public sealed record CodeCountRow(string? Code, int Count);
    public sealed record PairCountRow(string? Native, string? Learning, int Count);
    public sealed record GroupStatRow(string? Key, long Total, long Done, long Failed, long Processing, double? AvgSeconds);
    public sealed record TextCountRow(string? Text, long Count);
    public sealed record TopUserRow(Guid UserId, long Count);
}

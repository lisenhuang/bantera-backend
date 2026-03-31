namespace BanteraApi.Storage;

public class R2Settings
{
    public const string Section = "R2";

    public string AccountId { get; init; } = string.Empty;
    public string AccessKeyId { get; init; } = string.Empty;
    public string SecretAccessKey { get; init; } = string.Empty;
    public string BucketName { get; init; } = string.Empty;

    public string ServiceUrl => $"https://{AccountId}.r2.cloudflarestorage.com";
}

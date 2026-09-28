namespace BanteraApi.Chat;

public sealed class CloudflareTurnSettings
{
    public const string Section = "CloudflareTurn";

    public bool Enabled { get; set; }
    public string KeyId { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    // Existing clients do not refresh ICE credentials during a call.
    public int CredentialTtlSeconds { get; set; } = 86400;

    public bool IsConfigured => Enabled
        && Guid.TryParseExact(KeyId, "N", out _)
        && !string.IsNullOrWhiteSpace(ApiToken)
        && !ApiToken.StartsWith("SET_VIA_ENV_", StringComparison.Ordinal)
        && CredentialTtlSeconds is >= 60 and <= 86400;
}

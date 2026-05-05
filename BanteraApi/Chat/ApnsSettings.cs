namespace BanteraApi.Chat;

public sealed class ApnsSettings
{
    public const string Section = "Apns";
    public const string EnvironmentAuto = "Auto";
    public const string EnvironmentSandbox = "Sandbox";
    public const string EnvironmentProduction = "Production";
    public const string SandboxEndpoint = "https://api.sandbox.push.apple.com";
    public const string ProductionEndpoint = "https://api.push.apple.com";

    public string? KeyId { get; set; }
    public string? TeamId { get; set; }
    public string? BundleId { get; set; }
    public string? PrivateKeyPem { get; set; }
    public string? Environment { get; set; }

    public bool HasConfiguration =>
        !string.IsNullOrWhiteSpace(KeyId)
        && !string.IsNullOrWhiteSpace(TeamId)
        && !string.IsNullOrWhiteSpace(BundleId)
        && !string.IsNullOrWhiteSpace(PrivateKeyPem);

    public string EnvironmentMode
    {
        get
        {
            var normalized = Environment?.Trim();
            return normalized switch
            {
                var value when string.Equals(value, EnvironmentSandbox, StringComparison.OrdinalIgnoreCase) => EnvironmentSandbox,
                var value when string.Equals(value, EnvironmentProduction, StringComparison.OrdinalIgnoreCase) => EnvironmentProduction,
                _ => EnvironmentAuto,
            };
        }
    }

    public bool EffectiveSandbox(bool tokenIsSandbox)
    {
        return EnvironmentMode switch
        {
            EnvironmentSandbox => true,
            EnvironmentProduction => false,
            _ => tokenIsSandbox,
        };
    }
}

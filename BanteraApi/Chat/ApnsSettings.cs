namespace BanteraApi.Chat;

public sealed class ApnsSettings
{
    public const string Section = "Apns";

    public string? KeyId { get; set; }
    public string? TeamId { get; set; }
    public string? BundleId { get; set; }
    public string? PrivateKeyPem { get; set; }
}

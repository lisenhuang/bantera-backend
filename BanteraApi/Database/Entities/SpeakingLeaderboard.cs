namespace BanteraApi.Database.Entities;

public sealed class SpeakingVisibility
{
    public Guid UserId { get; set; }
    public bool ShowProfile { get; set; }
}

public sealed class SpeakingDevice
{
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public DateOnly? LegacyThroughDate { get; set; }
}

public sealed class SpeakingDay
{
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public DateOnly Date { get; set; }
    public string Language { get; set; } = "";
    public long SpokenWords { get; set; }
}

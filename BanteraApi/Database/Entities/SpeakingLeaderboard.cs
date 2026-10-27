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

// Timestamped counts only: no transcript or audio is stored in this ledger.
public sealed class SpeakingEvent
{
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid EventId { get; set; }
    public DateTime OccurredAt { get; set; }
    public string Language { get; set; } = "";
    public long SpokenWords { get; set; }
}

// A cumulative local-day total with bounds, not invented recording timestamps.
public sealed class SpeakingDayWindow
{
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public DateOnly Date { get; set; }
    public string Language { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime ThroughUtc { get; set; }
    public long SpokenWords { get; set; }
}

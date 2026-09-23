namespace BanteraApi.Database.Entities;

/// <summary>
/// One row per user per UTC day on which they made an authenticated app request.
/// This is the source of truth for DAU/WAU/MAU.
///
/// Deliberately has NO foreign key to users: accounts are hard-deleted, and a cascade
/// would retroactively shrink historical active-user counts. The row holds no personal
/// data beyond the user id.
/// </summary>
public class UserActivityDaily
{
    public Guid UserId { get; set; }

    /// <summary>The UTC calendar day.</summary>
    public DateOnly Date { get; set; }

    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    /// <summary>Number of ~hourly windows in which the user made a request — an engagement proxy.</summary>
    public int TouchCount { get; set; }

    /// <summary>Durable chat-message counter: chat_messages themselves are deleted after 7 days.</summary>
    public int MessagesSent { get; set; }

    /// <summary>"live" for rows written by the tracking middleware, "backfill" for reconstructed history.</summary>
    public string Source { get; set; } = ActivitySources.Live;
}

public static class ActivitySources
{
    public const string Live = "live";
    public const string Backfill = "backfill";
}

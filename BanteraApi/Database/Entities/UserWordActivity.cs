namespace BanteraApi.Database.Entities;

// Cumulative snapshots per installation and local calendar day. Keeping the
// device in the key makes retries idempotent and lets multiple devices add up.
public sealed class UserWordActivity
{
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public DateOnly Date { get; set; }
    public long ListenedWords { get; set; }
    public long SpokenWords { get; set; }
}

namespace BanteraApi.Database.Entities;

// Language-tagged snapshots use a separate table so old API binaries and
// unlabelled historical snapshots keep their existing keys and retry behavior.
public sealed class UserLanguageWordActivity
{
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public DateOnly Date { get; set; }
    public string Language { get; set; } = "";
    public long ListenedWords { get; set; }
    public long SpokenWords { get; set; }
}

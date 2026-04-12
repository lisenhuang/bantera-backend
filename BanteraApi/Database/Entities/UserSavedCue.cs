namespace BanteraApi.Database.Entities;

public class UserSavedCue
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid VideoId { get; set; }
    public string CueId { get; set; } = string.Empty;
    public int CueIndex { get; set; }
    public DateTime SavedAt { get; set; }

    public User? User { get; set; }
    public UserVideo? Video { get; set; }
}

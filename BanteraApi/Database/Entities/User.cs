namespace BanteraApi.Database.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "active";
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<UserIdentity> Identities { get; set; } = [];
    public ICollection<UserSession> Sessions { get; set; } = [];
}

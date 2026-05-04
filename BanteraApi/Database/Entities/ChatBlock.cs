namespace BanteraApi.Database.Entities;

public class ChatBlock
{
    public Guid Id { get; set; }
    public Guid BlockerUserId { get; set; }
    public Guid BlockedUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public User BlockerUser { get; set; } = null!;
    public User BlockedUser { get; set; } = null!;
}

namespace BanteraApi.Database.Entities;

public class ChatThreadMembership
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public Guid UserId { get; set; }
    public bool IsMuted { get; set; }
    public int UnreadCount { get; set; }
    public Guid? LastReadMessageId { get; set; }
    public DateTime? LastReadAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ChatThread Thread { get; set; } = null!;
    public User User { get; set; } = null!;
}

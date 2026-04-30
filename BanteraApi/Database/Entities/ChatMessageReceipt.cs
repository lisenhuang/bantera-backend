namespace BanteraApi.Database.Entities;

public class ChatMessageReceipt
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public DateTime ReceivedAt { get; set; }

    public ChatMessage Message { get; set; } = null!;
    public User User { get; set; } = null!;
}

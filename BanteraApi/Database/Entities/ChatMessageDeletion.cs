namespace BanteraApi.Database.Entities;

// No message content is retained. These IDs distinguish manual removal from expiry.
public sealed class ChatMessageDeletion
{
    public Guid MessageId { get; set; }
    public Guid ThreadId { get; set; }
    public DateTime DeletedAt { get; set; }
}

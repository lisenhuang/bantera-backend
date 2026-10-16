namespace BanteraApi.Database.Entities;

public class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public Guid SenderUserId { get; set; }
    public string AudioObjectKey { get; set; } = string.Empty;
    public string AudioContentType { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string SpokenLanguageCode { get; set; } = string.Empty;
    public string? AudioSha256 { get; set; }
    public long? AudioByteCount { get; set; }
    public int DurationMs { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public ChatThread Thread { get; set; } = null!;
    public User SenderUser { get; set; } = null!;
    public ICollection<ChatMessageReceipt> Receipts { get; set; } = [];
}

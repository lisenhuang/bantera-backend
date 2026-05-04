namespace BanteraApi.Database.Entities;

public class ChatThread
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? DirectMessageKey { get; set; }
    public string? LanguageKey { get; set; }
    public string? LanguageDisplayName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastMessageAt { get; set; }

    public ICollection<ChatThreadMembership> Memberships { get; set; } = [];
    public ICollection<ChatMessage> Messages { get; set; } = [];
}

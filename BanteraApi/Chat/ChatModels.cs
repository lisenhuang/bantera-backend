using Microsoft.AspNetCore.Http;
using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Chat;

public static class ChatThreadTypes
{
    public const string DirectMessage = "dm";
    public const string Group = "group";
}

public static class ChatGroupKinds
{
    public const string Learning = "learning";
    public const string Native = "native";
}

public static class ChatErrorCodes
{
    public const string ChatNotFound = "chat_not_found";
    public const string ChatForbidden = "chat_forbidden";
    public const string ChatBlocked = "chat_blocked";
    public const string ChatInvalidAudio = "chat_invalid_audio";
    public const string ChatInvalidLanguage = "chat_invalid_language";
}

public record ChatUserResponse(
    Guid Id,
    string Name,
    string? AvatarUrl,
    string? LearningLanguage,
    string? LearningLanguageDisplay,
    string? NativeLanguage,
    string? NativeLanguageDisplay,
    bool IsOnline
);

public record ChatMessageResponse(
    Guid MessageId,
    Guid ThreadId,
    string ThreadType,
    ChatUserResponse SenderUser,
    string SpokenLanguageCode,
    int DurationMs,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    bool IsMine,
    string AudioUrl
);

public record ChatThreadSummaryResponse(
    Guid ThreadId,
    string ThreadType,
    string Title,
    string? AvatarUrl,
    string? LearningLanguage,
    string? LearningLanguageDisplay,
    string? NativeLanguage,
    string? NativeLanguageDisplay,
    bool IsMuted,
    int UnreadCount,
    DateTime? LastMessageAt,
    int? LastMessageDurationMs,
    ChatUserResponse? OtherUser,
    IReadOnlyList<string> RoleBadges
);

public record ChatBootstrapResponse(
    bool GlobalNotificationsEnabled,
    IReadOnlyList<ChatThreadSummaryResponse> Groups,
    IReadOnlyList<ChatUserResponse> OnlineUsers,
    IReadOnlyList<ChatThreadSummaryResponse> DirectMessages
);

public sealed class SendChatAudioRequest
{
    [SwaggerSchema("Audio file to send. iPhone chat uses AAC/M4A.")]
    public IFormFile? File { get; set; }

    [SwaggerSchema("Duration in milliseconds, capped at 60 seconds.")]
    public int DurationMs { get; set; }
}

public sealed class UpdateChatNotificationsRequest
{
    public bool Enabled { get; set; }
}

public sealed class RegisterPushTokenRequest
{
    public string Token { get; set; } = string.Empty;
    public bool IsSandbox { get; set; }
}

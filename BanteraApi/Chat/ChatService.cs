using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Chat;

public class ChatService(
    AppDbContext db,
    R2StorageService r2StorageService,
    LinkGenerator linkGenerator,
    ChatRealtimeService realtimeService,
    ChatPushNotificationService pushNotificationService,
    ILogger<ChatService> logger)
{
    private static readonly HashSet<string> SupportedAudioContentTypes =
    [
        "audio/mp4",
        "audio/m4a",
        "audio/x-m4a",
        "audio/aac",
        "audio/mpeg",
        "audio/wav",
        "audio/x-wav",
        "audio/webm",
        "audio/ogg",
    ];

    private const long MaxAudioBytes = 20L * 1024 * 1024;
    private const int MaxDurationMs = 60_000;

    public async Task<ChatBootstrapResponse?> GetBootstrapAsync(
        Guid userId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, cancellationToken);
        if (user is null)
            return null;

        var groupEntries = await EnsureUserGroupThreadsAsync(user, cancellationToken);
        var groupKeys = groupEntries
            .Select(entry => entry.Descriptor.MatchKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hiddenUserIds = await GetHiddenUserIdsAsync(userId, cancellationToken);
        var onlineIds = realtimeService.SnapshotOnlineUserIds();

        var groups = new List<ChatThreadSummaryResponse>();
        foreach (var entry in groupEntries
                     .GroupBy(x => x.Thread.Id)
                     .Select(g => new
                     {
                         Entry = g.First(),
                         RoleBadges = g.Select(x => x.RoleBadge).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                     }))
        {
            groups.Add(await BuildGroupThreadSummaryAsync(
                entry.Entry.Thread,
                entry.Entry.Membership,
                entry.Entry.Descriptor,
                entry.RoleBadges,
                cancellationToken));
        }

        var onlineUsers = await db.Users
            .AsNoTracking()
            .Where(u => u.Id != userId && u.DeletedAt == null && u.Status == "active")
            .Where(u => onlineIds.Contains(u.Id))
            .ToListAsync(cancellationToken);

        var filteredOnlineUsers = onlineUsers
            .Where(u => !hiddenUserIds.Contains(u.Id))
            .Where(u =>
                ChatLanguageResolver.Resolve(u.LearningLanguage) is { } learning && groupKeys.Contains(learning.MatchKey)
                || ChatLanguageResolver.Resolve(u.NativeLanguage) is { } native && groupKeys.Contains(native.MatchKey))
            .OrderBy(u => u.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(u => BuildUserResponse(u, httpContext, true))
            .ToList();

        var dmMemberships = await db.ChatThreadMemberships
            .AsNoTracking()
            .Include(m => m.Thread)
            .Where(m => m.UserId == userId
                     && m.Thread.Type == ChatThreadTypes.DirectMessage
                     && m.DeletedAt == null)
            .OrderByDescending(m => m.Thread.LastMessageAt ?? m.Thread.UpdatedAt)
            .ToListAsync(cancellationToken);

        var directMessages = new List<ChatThreadSummaryResponse>();
        foreach (var membership in dmMemberships)
        {
            var summary = await BuildDirectMessageSummaryAsync(
                membership.Thread,
                membership,
                userId,
                httpContext,
                cancellationToken);
            if (summary?.OtherUser is { } otherUser && hiddenUserIds.Contains(otherUser.Id))
                continue;

            if (summary is not null)
                directMessages.Add(summary);
        }

        return new ChatBootstrapResponse(
            user.ChatNotificationsEnabled,
            groups.OrderBy(g => GroupBadgeOrder(g.RoleBadges)).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            filteredOnlineUsers,
            directMessages);
    }

    public async Task<IReadOnlyList<ChatMessageResponse>?> ListMessagesAsync(
        Guid userId,
        Guid threadId,
        HttpContext httpContext,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var thread = await db.ChatThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == threadId, cancellationToken);
        if (thread is null)
            return null;

        if (!await CanAccessThreadAsync(userId, thread, cancellationToken))
            return null;

        var hiddenUserIds = thread.Type == ChatThreadTypes.Group
            ? await GetHiddenUserIdsAsync(userId, cancellationToken)
            : [];
        var now = DateTime.UtcNow;
        var safeLimit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 100);
        var safeOffset = Math.Max(offset, 0);

        var messages = await db.ChatMessages
            .AsNoTracking()
            .Include(m => m.SenderUser)
            .Where(m => m.ThreadId == threadId)
            .Where(m => m.ExpiresAt == null || m.ExpiresAt > now)
            .OrderByDescending(m => m.CreatedAt)
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);

        if (thread.Type == ChatThreadTypes.Group)
            messages = messages.Where(m => !hiddenUserIds.Contains(m.SenderUserId)).ToList();

        messages.Reverse();
        return messages
            .Select(message => BuildMessageResponse(message, thread.Type, userId, httpContext))
            .ToList();
    }

    public async Task<(ChatMessageResponse? Message, string? ErrorCode)> SendDirectMessageAudioAsync(
        Guid userId,
        Guid otherUserId,
        SendChatAudioRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (userId == otherUserId)
            return (null, ChatErrorCodes.ChatForbidden);

        var sender = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, cancellationToken);
        var recipient = await db.Users.FirstOrDefaultAsync(u => u.Id == otherUserId && u.DeletedAt == null, cancellationToken);
        if (sender is null || recipient is null)
            return (null, ChatErrorCodes.ChatNotFound);

        if (await AreUsersBlockedEitherDirectionAsync(userId, otherUserId, cancellationToken))
            return (null, ChatErrorCodes.ChatBlocked);

        var spokenLanguage = ChatLanguageResolver.Resolve(sender.LearningLanguage);
        if (spokenLanguage is null)
            return (null, ChatErrorCodes.ChatInvalidLanguage);

        var validationError = ValidateAudioRequest(request);
        if (validationError is not null)
            return (null, validationError);

        var thread = await GetOrCreateDirectMessageThreadAsync(userId, otherUserId, cancellationToken);
        var senderMembership = await EnsureMembershipAsync(thread.Id, userId, cancellationToken);
        var recipientMembership = await EnsureMembershipAsync(thread.Id, otherUserId, cancellationToken);
        var now = DateTime.UtcNow;

        senderMembership.DeletedAt = null;
        senderMembership.UpdatedAt = now;
        senderMembership.UnreadCount = 0;
        recipientMembership.DeletedAt = null;
        recipientMembership.UpdatedAt = now;
        recipientMembership.UnreadCount += 1;

        var (contentType, fileName) = NormalizeAudioFile(request.File!);
        var messageId = Guid.NewGuid();
        var message = new ChatMessage
        {
            Id = messageId,
            ThreadId = thread.Id,
            SenderUserId = userId,
            AudioObjectKey = BuildAudioObjectKey(thread.Id, messageId, contentType),
            AudioContentType = contentType,
            OriginalFileName = fileName,
            SpokenLanguageCode = spokenLanguage.OriginalCode,
            DurationMs = request.DurationMs,
            CreatedAt = now,
        };

        await using (var input = request.File!.OpenReadStream())
        {
            await r2StorageService.UploadObjectAsync(
                message.AudioObjectKey,
                input,
                contentType,
                cancellationToken);
        }

        db.ChatMessages.Add(message);
        thread.LastMessageAt = now;
        thread.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        message.SenderUser = sender;
        var response = BuildMessageResponse(message, thread.Type, userId, httpContext);
        await NotifyUsersAboutThreadUpdateAsync([userId, otherUserId], thread.Id, cancellationToken);
        await realtimeService.SendToUserAsync(
            otherUserId,
            new { type = "message.created", payload = new { threadId = thread.Id, messageId = message.Id } },
            cancellationToken);

        if (recipient.ChatNotificationsEnabled && !recipientMembership.IsMuted && !realtimeService.IsUserOnline(otherUserId))
        {
            var tokens = await db.UserPushTokens
                .AsNoTracking()
                .Where(t => t.UserId == otherUserId)
                .ToListAsync(cancellationToken);
            await pushNotificationService.SendAsync(
                tokens,
                sender.Name ?? "Bantera user",
                "sent an audio message",
                new Dictionary<string, string>
                {
                    ["threadId"] = thread.Id.ToString(),
                    ["threadType"] = ChatThreadTypes.DirectMessage,
                },
                cancellationToken);
        }

        return (response, null);
    }

    public async Task<(ChatMessageResponse? Message, string? ErrorCode)> SendGroupAudioAsync(
        Guid userId,
        string groupKind,
        SendChatAudioRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var sender = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, cancellationToken);
        if (sender is null)
            return (null, ChatErrorCodes.ChatNotFound);

        var descriptor = ResolveGroupDescriptor(sender, groupKind);
        if (descriptor is null)
            return (null, ChatErrorCodes.ChatInvalidLanguage);

        var validationError = ValidateAudioRequest(request);
        if (validationError is not null)
            return (null, validationError);

        var thread = await GetOrCreateGroupThreadAsync(descriptor, cancellationToken);
        var now = DateTime.UtcNow;
        var expiresAt = now.AddDays(7);
        var blockPairs = await LoadBlockPairsAsync(cancellationToken);
        var audience = await db.Users
            .Where(u => u.DeletedAt == null && u.Status == "active")
            .ToListAsync(cancellationToken);

        var targetUsers = audience
            .Where(u => MatchesGroupKey(u, descriptor.MatchKey))
            .Where(u => !AreUsersBlockedEitherDirectionInMemory(userId, u.Id, blockPairs))
            .ToList();
        var targetUserIds = targetUsers.Select(u => u.Id).ToList();
        var memberships = await db.ChatThreadMemberships
            .Where(m => m.ThreadId == thread.Id && targetUserIds.Contains(m.UserId))
            .ToListAsync(cancellationToken);
        var membershipMap = memberships.ToDictionary(m => m.UserId);

        foreach (var targetUser in targetUsers)
        {
            if (!membershipMap.TryGetValue(targetUser.Id, out var membership))
            {
                membership = new ChatThreadMembership
                {
                    ThreadId = thread.Id,
                    UserId = targetUser.Id,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.ChatThreadMemberships.Add(membership);
                membershipMap[targetUser.Id] = membership;
            }

            membership.DeletedAt = null;
            membership.UpdatedAt = now;
            membership.UnreadCount = targetUser.Id == userId ? 0 : membership.UnreadCount + 1;
        }

        var (contentType, fileName) = NormalizeAudioFile(request.File!);
        var messageId = Guid.NewGuid();
        var message = new ChatMessage
        {
            Id = messageId,
            ThreadId = thread.Id,
            SenderUserId = userId,
            AudioObjectKey = BuildAudioObjectKey(thread.Id, messageId, contentType),
            AudioContentType = contentType,
            OriginalFileName = fileName,
            SpokenLanguageCode = descriptor.OriginalCode,
            DurationMs = request.DurationMs,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

        await using (var input = request.File!.OpenReadStream())
        {
            await r2StorageService.UploadObjectAsync(
                message.AudioObjectKey,
                input,
                contentType,
                cancellationToken);
        }

        db.ChatMessages.Add(message);
        thread.LastMessageAt = now;
        thread.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        await NotifyUsersAboutThreadUpdateAsync(targetUserIds, thread.Id, cancellationToken);
        await realtimeService.SendToUsersAsync(
            targetUserIds,
            new { type = "message.created", payload = new { threadId = thread.Id, messageId = message.Id } },
            cancellationToken);

        var pushRecipients = targetUsers
            .Where(u => u.Id != userId)
            .Where(u => u.ChatNotificationsEnabled)
            .Where(u => membershipMap.TryGetValue(u.Id, out var membership) && !membership.IsMuted)
            .Where(u => !realtimeService.IsUserOnline(u.Id))
            .Select(u => u.Id)
            .ToList();

        if (pushRecipients.Count > 0)
        {
            var tokens = await db.UserPushTokens
                .AsNoTracking()
                .Where(t => pushRecipients.Contains(t.UserId))
                .ToListAsync(cancellationToken);
            await pushNotificationService.SendAsync(
                tokens,
                descriptor.DisplayName,
                "New audio message in group",
                new Dictionary<string, string>
                {
                    ["threadId"] = thread.Id.ToString(),
                    ["threadType"] = ChatThreadTypes.Group,
                },
                cancellationToken);
        }

        message.SenderUser = sender;
        return (BuildMessageResponse(message, thread.Type, userId, httpContext), null);
    }

    public async Task<StoredObjectResult?> GetMessageAudioAsync(
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        var message = await db.ChatMessages
            .AsNoTracking()
            .Include(m => m.Thread)
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null || message.ExpiresAt != null && message.ExpiresAt <= DateTime.UtcNow)
            return null;

        if (!await CanAccessMessageAsync(userId, message, cancellationToken))
            return null;

        return await r2StorageService.DownloadObjectAsync(message.AudioObjectKey, cancellationToken);
    }

    public async Task<bool> AcknowledgeReceivedAsync(
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        var message = await db.ChatMessages
            .Include(m => m.Thread)
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null || message.Thread.Type != ChatThreadTypes.DirectMessage)
            return false;

        var membershipExists = await db.ChatThreadMemberships
            .AnyAsync(m => m.ThreadId == message.ThreadId && m.UserId == userId, cancellationToken);
        if (!membershipExists)
            return false;

        var existing = await db.ChatMessageReceipts
            .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId, cancellationToken);
        if (existing is null)
        {
            var now = DateTime.UtcNow;
            db.ChatMessageReceipts.Add(new ChatMessageReceipt
            {
                MessageId = messageId,
                UserId = userId,
                ReceivedAt = now,
            });
            message.ExpiresAt = now.AddDays(7);
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task<bool> MarkThreadReadAsync(
        Guid userId,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        var membership = await db.ChatThreadMemberships
            .FirstOrDefaultAsync(m => m.ThreadId == threadId && m.UserId == userId, cancellationToken);
        if (membership is null)
            return false;

        membership.UnreadCount = 0;
        membership.LastReadAt = DateTime.UtcNow;
        membership.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateThreadNotificationsAsync(
        Guid userId,
        Guid threadId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var membership = await db.ChatThreadMemberships
            .FirstOrDefaultAsync(m => m.ThreadId == threadId && m.UserId == userId, cancellationToken);
        if (membership is null)
            return false;

        membership.IsMuted = !enabled;
        membership.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateGlobalNotificationsAsync(
        Guid userId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return false;

        user.ChatNotificationsEnabled = enabled;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RegisterPushTokenAsync(
        Guid userId,
        string token,
        bool isSandbox,
        CancellationToken cancellationToken = default)
    {
        var normalized = token.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var existingForToken = await db.UserPushTokens
            .Where(t => t.Token == normalized)
            .ToListAsync(cancellationToken);
        var existing = existingForToken.FirstOrDefault(t => t.UserId == userId);
        var now = DateTime.UtcNow;

        db.UserPushTokens.RemoveRange(existingForToken.Where(t => t.UserId != userId));
        if (existing is null)
        {
            db.UserPushTokens.Add(new UserPushToken
            {
                UserId = userId,
                Token = normalized,
                Platform = "ios",
                IsSandbox = isSandbox,
                CreatedAt = now,
                UpdatedAt = now,
                LastSeenAt = now,
            });
        }
        else
        {
            existing.IsSandbox = isSandbox;
            existing.LastSeenAt = now;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> BlockUserAsync(
        Guid userId,
        Guid otherUserId,
        CancellationToken cancellationToken = default)
    {
        if (userId == otherUserId)
            return false;

        var otherExists = await db.Users.AnyAsync(u => u.Id == otherUserId && u.DeletedAt == null, cancellationToken);
        if (!otherExists)
            return false;

        var existing = await db.ChatBlocks
            .FirstOrDefaultAsync(b => b.BlockerUserId == userId && b.BlockedUserId == otherUserId, cancellationToken);
        if (existing is null)
        {
            db.ChatBlocks.Add(new ChatBlock
            {
                BlockerUserId = userId,
                BlockedUserId = otherUserId,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await NotifyUsersAboutFullRefreshAsync([userId, otherUserId], cancellationToken);
        return true;
    }

    public async Task<bool> UnblockUserAsync(
        Guid userId,
        Guid otherUserId,
        CancellationToken cancellationToken = default)
    {
        var blocks = await db.ChatBlocks
            .Where(b => b.BlockerUserId == userId && b.BlockedUserId == otherUserId)
            .ToListAsync(cancellationToken);
        if (blocks.Count == 0)
            return false;

        db.ChatBlocks.RemoveRange(blocks);
        await db.SaveChangesAsync(cancellationToken);
        await NotifyUsersAboutFullRefreshAsync([userId, otherUserId], cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ChatUserResponse>> ListBlockedUsersAsync(
        Guid userId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var blockedUsers = await db.ChatBlocks
            .AsNoTracking()
            .Where(b => b.BlockerUserId == userId)
            .Join(db.Users, b => b.BlockedUserId, u => u.Id, (_, u) => u)
            .Where(u => u.DeletedAt == null)
            .OrderBy(u => u.Name ?? string.Empty)
            .ToListAsync(cancellationToken);

        return blockedUsers
            .Select(u => BuildUserResponse(u, httpContext, realtimeService.IsUserOnline(u.Id)))
            .ToList();
    }

    public async Task<bool> DeleteDirectMessageForSelfAsync(
        Guid userId,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        var membership = await db.ChatThreadMemberships
            .Include(m => m.Thread)
            .FirstOrDefaultAsync(m => m.ThreadId == threadId && m.UserId == userId, cancellationToken);
        if (membership is null || membership.Thread.Type != ChatThreadTypes.DirectMessage)
            return false;

        membership.DeletedAt = DateTime.UtcNow;
        membership.UnreadCount = 0;
        membership.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await NotifyUsersAboutThreadUpdateAsync([userId], threadId, cancellationToken);
        return true;
    }

    public async Task<(bool Ok, string? ErrorCode)> DeleteOwnMessageAsync(
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        var message = await db.ChatMessages
            .Include(m => m.Thread)
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null)
            return (false, ChatErrorCodes.ChatNotFound);
        if (message.SenderUserId != userId)
            return (false, ChatErrorCodes.ChatForbidden);

        var memberIds = await db.ChatThreadMemberships
            .Where(m => m.ThreadId == message.ThreadId && m.DeletedAt == null)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

        db.ChatMessages.Remove(message);
        await db.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(message.AudioObjectKey))
        {
            try
            {
                await r2StorageService.DeleteObjectAsync(message.AudioObjectKey, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete chat audio {Key} for message {MessageId}.", message.AudioObjectKey, messageId);
            }
        }

        await NotifyUsersAboutThreadUpdateAsync(memberIds, message.ThreadId, cancellationToken);
        return (true, null);
    }

    public async Task<bool> ForwardRecordingStatusAsync(
        Guid userId,
        Guid threadId,
        bool isRecording,
        CancellationToken cancellationToken = default)
    {
        var membership = await db.ChatThreadMemberships
            .Include(m => m.Thread)
            .FirstOrDefaultAsync(m => m.ThreadId == threadId && m.UserId == userId, cancellationToken);
        if (membership is null || membership.Thread.Type != ChatThreadTypes.DirectMessage)
            return false;

        var otherUserId = await db.ChatThreadMemberships
            .Where(m => m.ThreadId == threadId && m.UserId != userId)
            .Select(m => m.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (otherUserId == Guid.Empty)
            return false;

        if (await AreUsersBlockedEitherDirectionAsync(userId, otherUserId, cancellationToken))
            return false;

        await realtimeService.SendToUserAsync(
            otherUserId,
            new
            {
                type = isRecording ? "dm.recording.started" : "dm.recording.stopped",
                payload = new { threadId, userId }
            },
            cancellationToken);
        return true;
    }

    public async Task DeleteChatAudioForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var keys = await db.ChatMessages
            .Where(m => m.SenderUserId == userId)
            .Select(m => m.AudioObjectKey)
            .ToListAsync(cancellationToken);

        foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            try
            {
                await r2StorageService.DeleteObjectAsync(key, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete chat audio {Key} during account deletion.", key);
            }
        }
    }

    private async Task<List<(ChatThread Thread, ChatThreadMembership Membership, ChatLanguageDescriptor Descriptor, string RoleBadge)>> EnsureUserGroupThreadsAsync(
        User user,
        CancellationToken cancellationToken)
    {
        var entries = new List<(ChatThread, ChatThreadMembership, ChatLanguageDescriptor, string)>();

        var learning = ResolveGroupDescriptor(user, ChatGroupKinds.Learning);
        if (learning is not null)
        {
            var thread = await GetOrCreateGroupThreadAsync(learning, cancellationToken);
            var membership = await EnsureMembershipAsync(thread.Id, user.Id, cancellationToken);
            entries.Add((thread, membership, learning, "Learning"));
        }

        var native = ResolveGroupDescriptor(user, ChatGroupKinds.Native);
        if (native is not null)
        {
            var thread = await GetOrCreateGroupThreadAsync(native, cancellationToken);
            var membership = await EnsureMembershipAsync(thread.Id, user.Id, cancellationToken);
            entries.Add((thread, membership, native, "Native"));
        }

        return entries;
    }

    private async Task<ChatThread> GetOrCreateDirectMessageThreadAsync(Guid userId, Guid otherUserId, CancellationToken cancellationToken)
    {
        var key = BuildDirectMessageKey(userId, otherUserId);
        var thread = await db.ChatThreads
            .FirstOrDefaultAsync(t => t.Type == ChatThreadTypes.DirectMessage && t.DirectMessageKey == key, cancellationToken);
        if (thread is not null)
            return thread;

        thread = new ChatThread
        {
            Type = ChatThreadTypes.DirectMessage,
            DirectMessageKey = key,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.ChatThreads.Add(thread);
        await db.SaveChangesAsync(cancellationToken);
        return thread;
    }

    private async Task<ChatThread> GetOrCreateGroupThreadAsync(ChatLanguageDescriptor descriptor, CancellationToken cancellationToken)
    {
        var thread = await db.ChatThreads
            .FirstOrDefaultAsync(t => t.Type == ChatThreadTypes.Group && t.LanguageKey == descriptor.MatchKey, cancellationToken);
        if (thread is not null)
            return thread;

        thread = new ChatThread
        {
            Type = ChatThreadTypes.Group,
            LanguageKey = descriptor.MatchKey,
            LanguageDisplayName = descriptor.DisplayName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.ChatThreads.Add(thread);
        await db.SaveChangesAsync(cancellationToken);
        return thread;
    }

    private async Task<ChatThreadMembership> EnsureMembershipAsync(Guid threadId, Guid userId, CancellationToken cancellationToken)
    {
        var membership = await db.ChatThreadMemberships
            .FirstOrDefaultAsync(m => m.ThreadId == threadId && m.UserId == userId, cancellationToken);
        if (membership is not null)
            return membership;

        membership = new ChatThreadMembership
        {
            ThreadId = threadId,
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.ChatThreadMemberships.Add(membership);
        await db.SaveChangesAsync(cancellationToken);
        return membership;
    }

    private static ChatLanguageDescriptor? ResolveGroupDescriptor(User user, string groupKind)
    {
        return groupKind switch
        {
            ChatGroupKinds.Learning => ChatLanguageResolver.Resolve(user.LearningLanguage),
            ChatGroupKinds.Native => ChatLanguageResolver.Resolve(user.NativeLanguage),
            _ => null,
        };
    }

    private async Task<bool> CanAccessThreadAsync(Guid userId, ChatThread thread, CancellationToken cancellationToken)
    {
        if (thread.Type == ChatThreadTypes.DirectMessage)
        {
            return await db.ChatThreadMemberships
                .AnyAsync(m => m.ThreadId == thread.Id && m.UserId == userId, cancellationToken);
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(thread.LanguageKey))
            return false;

        return MatchesGroupKey(user, thread.LanguageKey);
    }

    private async Task<bool> CanAccessMessageAsync(Guid userId, ChatMessage message, CancellationToken cancellationToken)
    {
        if (!await CanAccessThreadAsync(userId, message.Thread, cancellationToken))
            return false;

        if (message.Thread.Type != ChatThreadTypes.Group)
            return true;

        var hiddenUserIds = await GetHiddenUserIdsAsync(userId, cancellationToken);
        return !hiddenUserIds.Contains(message.SenderUserId);
    }

    private async Task<HashSet<Guid>> GetHiddenUserIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await db.ChatBlocks
            .AsNoTracking()
            .Where(b => b.BlockerUserId == userId || b.BlockedUserId == userId)
            .Select(b => b.BlockerUserId == userId ? b.BlockedUserId : b.BlockerUserId)
            .ToHashSetAsync(cancellationToken);
    }

    private async Task<HashSet<(Guid, Guid)>> LoadBlockPairsAsync(CancellationToken cancellationToken)
    {
        var pairs = await db.ChatBlocks
            .AsNoTracking()
            .Select(b => new { b.BlockerUserId, b.BlockedUserId })
            .ToListAsync(cancellationToken);
        return pairs.Select(p => (p.BlockerUserId, p.BlockedUserId)).ToHashSet();
    }

    private async Task<bool> AreUsersBlockedEitherDirectionAsync(Guid userId, Guid otherUserId, CancellationToken cancellationToken)
    {
        return await db.ChatBlocks.AnyAsync(
            b => (b.BlockerUserId == userId && b.BlockedUserId == otherUserId)
              || (b.BlockerUserId == otherUserId && b.BlockedUserId == userId),
            cancellationToken);
    }

    private static bool AreUsersBlockedEitherDirectionInMemory(Guid userId, Guid otherUserId, HashSet<(Guid, Guid)> blockPairs)
    {
        return blockPairs.Contains((userId, otherUserId)) || blockPairs.Contains((otherUserId, userId));
    }

    private static bool MatchesGroupKey(User user, string groupKey)
    {
        return ChatLanguageResolver.MatchesAny(user.LearningLanguage, [groupKey])
            || ChatLanguageResolver.MatchesAny(user.NativeLanguage, [groupKey]);
    }

    private async Task<ChatThreadSummaryResponse> BuildGroupThreadSummaryAsync(
        ChatThread thread,
        ChatThreadMembership membership,
        ChatLanguageDescriptor descriptor,
        IReadOnlyList<string> roleBadges,
        CancellationToken cancellationToken)
    {
        var latestMessage = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.ThreadId == thread.Id)
            .Where(m => m.ExpiresAt == null || m.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new ChatThreadSummaryResponse(
            thread.Id,
            ChatThreadTypes.Group,
            descriptor.DisplayName,
            null,
            null,
            null,
            null,
            null,
            membership.IsMuted,
            membership.UnreadCount,
            latestMessage?.CreatedAt ?? thread.LastMessageAt,
            latestMessage?.DurationMs,
            null,
            roleBadges);
    }

    private async Task<ChatThreadSummaryResponse?> BuildDirectMessageSummaryAsync(
        ChatThread thread,
        ChatThreadMembership membership,
        Guid requesterUserId,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var otherMembership = await db.ChatThreadMemberships
            .AsNoTracking()
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.ThreadId == thread.Id && m.UserId != requesterUserId, cancellationToken);
        if (otherMembership is null)
            return null;

        var latestMessage = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.ThreadId == thread.Id)
            .Where(m => m.ExpiresAt == null || m.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var otherUser = BuildUserResponse(
            otherMembership.User,
            httpContext,
            realtimeService.IsUserOnline(otherMembership.UserId));

        return new ChatThreadSummaryResponse(
            thread.Id,
            ChatThreadTypes.DirectMessage,
            otherUser.Name,
            otherUser.AvatarUrl,
            otherUser.LearningLanguage,
            otherUser.LearningLanguageDisplay,
            otherUser.NativeLanguage,
            otherUser.NativeLanguageDisplay,
            membership.IsMuted,
            membership.UnreadCount,
            latestMessage?.CreatedAt ?? thread.LastMessageAt,
            latestMessage?.DurationMs,
            otherUser,
            []);
    }

    private ChatMessageResponse BuildMessageResponse(
        ChatMessage message,
        string threadType,
        Guid requesterUserId,
        HttpContext httpContext)
    {
        var sender = message.SenderUser;
        var senderUser = BuildUserResponse(sender, httpContext, realtimeService.IsUserOnline(sender.Id));
        return new ChatMessageResponse(
            message.Id,
            message.ThreadId,
            threadType,
            senderUser,
            message.SpokenLanguageCode,
            message.DurationMs,
            message.CreatedAt,
            message.ExpiresAt,
            message.SenderUserId == requesterUserId,
            linkGenerator.GetUriByName(
                httpContext,
                "GetChatMessageAudio",
                values: new { messageId = message.Id }) ?? string.Empty);
    }

    private ChatUserResponse BuildUserResponse(User user, HttpContext httpContext, bool isOnline)
    {
        return new ChatUserResponse(
            user.Id,
            ResolveUserName(user),
            BuildAvatarUrl(user, httpContext),
            ChatLanguageResolver.Resolve(user.LearningLanguage)?.OriginalCode,
            ChatLanguageResolver.Resolve(user.LearningLanguage)?.ExactDisplayName,
            ChatLanguageResolver.Resolve(user.NativeLanguage)?.OriginalCode,
            ChatLanguageResolver.Resolve(user.NativeLanguage)?.ExactDisplayName,
            isOnline);
    }

    private static string ResolveUserName(User user)
    {
        return string.IsNullOrWhiteSpace(user.Name) ? "Bantera user" : user.Name;
    }

    private string? BuildAvatarUrl(User user, HttpContext httpContext)
    {
        if (string.IsNullOrWhiteSpace(user.AvatarObjectKey))
            return null;

        return linkGenerator.GetUriByName(
            httpContext,
            "GetUserAvatar",
            values: new { userId = user.Id, v = user.AvatarUpdatedAt?.Ticks });
    }

    private static string? ValidateAudioRequest(SendChatAudioRequest request)
    {
        if (request.File is null
            || request.File.Length <= 0
            || request.File.Length > MaxAudioBytes
            || request.DurationMs <= 0
            || request.DurationMs > MaxDurationMs)
        {
            return ChatErrorCodes.ChatInvalidAudio;
        }

        var normalized = NormalizeAudioContentType(request.File.ContentType, request.File.FileName);
        return normalized is null ? ChatErrorCodes.ChatInvalidAudio : null;
    }

    private static (string ContentType, string FileName) NormalizeAudioFile(IFormFile file)
    {
        var contentType = NormalizeAudioContentType(file.ContentType, file.FileName) ?? "audio/mp4";
        var fileName = string.IsNullOrWhiteSpace(file.FileName) ? $"chat-audio-{Guid.NewGuid():N}.m4a" : file.FileName.Trim();
        return (contentType, fileName);
    }

    private static string? NormalizeAudioContentType(string? contentType, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var normalized = contentType.Trim().ToLowerInvariant();
            if (SupportedAudioContentTypes.Contains(normalized))
                return normalized;
        }

        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".webm" => "audio/webm",
            ".ogg" => "audio/ogg",
            _ => null,
        };
    }

    private static string BuildAudioObjectKey(Guid threadId, Guid messageId, string contentType)
    {
        var extension = contentType switch
        {
            "audio/aac" => "aac",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/mpeg" => "mp3",
            "audio/webm" => "webm",
            "audio/ogg" => "ogg",
            _ => "m4a",
        };

        return $"chats/{threadId:N}/messages/{messageId:N}.{extension}";
    }

    private static string BuildDirectMessageKey(Guid userId, Guid otherUserId)
    {
        var pair = new[] { userId.ToString("N"), otherUserId.ToString("N") };
        Array.Sort(pair, StringComparer.Ordinal);
        return $"{pair[0]}:{pair[1]}";
    }

    private static int GroupBadgeOrder(IReadOnlyList<string> badges)
    {
        if (badges.Contains("Learning", StringComparer.OrdinalIgnoreCase)
            && badges.Contains("Native", StringComparer.OrdinalIgnoreCase))
        {
            return 0;
        }

        return badges.Contains("Learning", StringComparer.OrdinalIgnoreCase) ? 1 : 2;
    }

    private Task NotifyUsersAboutThreadUpdateAsync(IEnumerable<Guid> userIds, Guid threadId, CancellationToken cancellationToken)
    {
        return realtimeService.SendToUsersAsync(
            userIds,
            new { type = "thread.updated", payload = new { threadId } },
            cancellationToken);
    }

    private Task NotifyUsersAboutFullRefreshAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        return realtimeService.SendToUsersAsync(
            userIds,
            new { type = "thread.updated", payload = new { refresh = true } },
            cancellationToken);
    }
}

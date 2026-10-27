using System.Text.Json;
using BanteraApi.Chat;
using Xunit;
namespace BanteraApi.Tests;
public class ChatAvatarNotificationTests
{
    [Fact]
    public void SenderFieldsOptMessageIntoServiceExtensionWithoutChangingLegacyAlert()
    {
        var data = new Dictionary<string,string> {
            ["threadId"] = "thread", ["senderId"] = Guid.NewGuid().ToString(),
            ["senderName"] = "Sender", ["senderAvatarUrl"] = "https://api.bantera.app/api/users/avatar"
        };
        var value = JsonSerializer.SerializeToElement(ChatPushNotificationService.BuildPayload("Sender", "sent an audio message", data));
        Assert.Equal(1, value.GetProperty("aps").GetProperty("mutable-content").GetInt32());
        Assert.Equal("Sender", value.GetProperty("aps").GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("sent an audio message", value.GetProperty("aps").GetProperty("alert").GetProperty("body").GetString());
        Assert.Equal("thread", value.GetProperty("threadId").GetString());
    }
    [Fact]
    public void CallAndOtherPushesDoNotOptIntoMessageExtension()
    {
        var value = JsonSerializer.SerializeToElement(ChatPushNotificationService.BuildPayload("Caller", "Incoming call", new Dictionary<string,string> { ["callId"] = Guid.NewGuid().ToString() }));
        Assert.False(value.GetProperty("aps").TryGetProperty("mutable-content", out _));
    }
}

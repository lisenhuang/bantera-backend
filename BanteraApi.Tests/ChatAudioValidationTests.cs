using BanteraApi.Chat;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BanteraApi.Tests;

public class ChatAudioValidationTests
{
    [Theory]
    [InlineData(60_000, true, true)]
    [InlineData(60_001, true, true)]
    [InlineData(180_000, true, true)]
    [InlineData(180_001, true, false)]
    [InlineData(0, true, false)]
    [InlineData(60_000, false, true)]
    [InlineData(60_001, false, false)]
    public void ThreeMinuteDirectMessagesPreserveGroupLimit(int duration, bool direct, bool valid)
    {
        var request = new SendChatAudioRequest {
            DurationMs = duration,
            File = new FormFile(new MemoryStream(new byte[16]), 0, 16, "audio", "voice.m4a") {
                Headers = new HeaderDictionary(), ContentType = "audio/mp4"
            }
        };
        Assert.Equal(valid, ChatService.ValidateAudioRequest(request, direct) is null);
    }
}

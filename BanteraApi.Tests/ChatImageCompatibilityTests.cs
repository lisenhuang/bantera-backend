using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using BanteraApi.Chat;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BanteraApi.Tests;

public class ChatImageCompatibilityTests
{
    [WordActivityDatabaseFact]
    public async Task OldFeedRemainsAudioOnlyNewFeedIncludesImagesAndEnforcesGroupAccess()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_WORD_ACTIVITY_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("bantera_word_activity_verify", connection);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, WordActivityTests.TestAuth>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(new ChatRealtimeService(NullLogger<ChatRealtimeService>.Instance));
        builder.Services.AddScoped(sp => new ChatService(sp.GetRequiredService<AppDbContext>(), null!,
            sp.GetRequiredService<LinkGenerator>(), sp.GetRequiredService<ChatRealtimeService>(), null!, NullLogger<ChatService>.Instance));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        foreach (var version in new[] { "", "/v2" })
        {
            var includeImages = version == "/v2";
            app.MapGet($"/api{version}/chat/threads/{{threadId:guid}}/messages", async (Guid threadId, int limit,
                ClaimsPrincipal user, ChatService service, HttpContext context) =>
            {
                var messages = await service.ListMessagesAsync(Guid.Parse(user.FindFirstValue("sub")!), threadId, context, limit, 0, includeImages: includeImages);
                return messages is null ? Results.NotFound() : Results.Ok(messages);
            }).RequireAuthorization();
        }
        app.MapGet("/api/chat/messages/{messageId:guid}/audio", () => Results.Ok()).WithName("GetChatMessageAudio");
        app.MapGet("/api/v2/chat/messages/{messageId:guid}/image", () => Results.Ok()).WithName("GetChatMessageImage");
        var userId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var blockedId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var voiceId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(new User {Id=userId, NativeLanguage="zh-CN", LearningLanguage="en-NZ", CreatedAt=now, UpdatedAt=now},
                new User {Id=outsiderId, NativeLanguage="ja-JP", LearningLanguage="fr-FR", CreatedAt=now, UpdatedAt=now},
                new User {Id=blockedId, NativeLanguage="zh", LearningLanguage="en-US", CreatedAt=now, UpdatedAt=now});
            db.ChatThreads.Add(new ChatThread {Id=threadId, Type=ChatThreadTypes.Group, LanguageKey="zh", CreatedAt=now, UpdatedAt=now});
            db.ChatMessages.AddRange(
                new ChatMessage {Id=voiceId, ThreadId=threadId, SenderUserId=userId, AudioContentType="audio/mp4", AudioObjectKey="voice", DurationMs=1000, CreatedAt=now.AddSeconds(-10)},
                new ChatMessage {Id=imageId, ThreadId=threadId, SenderUserId=userId, AudioContentType="image/jpeg", AudioObjectKey="image", CreatedAt=now},
                new ChatMessage {Id=Guid.NewGuid(), ThreadId=threadId, SenderUserId=blockedId, AudioContentType="image/jpeg", AudioObjectKey="blocked", CreatedAt=now.AddSeconds(-5)});
            db.ChatBlocks.Add(new ChatBlock {Id=Guid.NewGuid(), BlockerUserId=userId, BlockedUserId=blockedId, CreatedAt=now});
            await db.SaveChangesAsync();
            var service = scope.ServiceProvider.GetRequiredService<ChatService>();
            var wrongGroup = await service.SendNativeGroupImageAsync(userId,
                new SendNativeGroupImageRequest {ExpectedNativeLanguage="en-NZ"}, new DefaultHttpContext());
            Assert.Equal(ChatErrorCodes.ChatInvalidLanguage, wrongGroup.ErrorCode);
            var nativeGroup = await service.SendNativeGroupImageAsync(userId,
                new SendNativeGroupImageRequest {ExpectedNativeLanguage="zh"}, new DefaultHttpContext());
            Assert.Equal(ChatErrorCodes.ChatInvalidImage, nativeGroup.ErrorCode);
        }
        await app.StartAsync();
        using var client = new HttpClient {BaseAddress=new Uri(app.Urls.Single())};
        try
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v2/chat/threads/{threadId}/messages?limit=100")).StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
            var old = await client.GetFromJsonAsync<ChatMessageResponse[]>($"/api/chat/threads/{threadId}/messages?limit=1");
            Assert.Equal(voiceId, Assert.Single(old!).MessageId);
            Assert.EndsWith("/audio", old![0].AudioUrl);
            var current = await client.GetFromJsonAsync<ChatMessageResponse[]>($"/api/v2/chat/threads/{threadId}/messages?limit=100");
            Assert.Equal(new[] {voiceId, imageId}, current!.Select(m => m.MessageId));
            Assert.EndsWith("/image", current!.Last().AudioUrl);
            client.DefaultRequestHeaders.Remove("X-Test-User");
            client.DefaultRequestHeaders.Add("X-Test-User", outsiderId.ToString());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/chat/threads/{threadId}/messages?limit=100")).StatusCode);
        }
        finally
        {
            await app.StopAsync();
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ChatThreads.Where(t => t.Id == threadId).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == userId || u.Id == outsiderId || u.Id == blockedId).ExecuteDeleteAsync();
        }
    }
}

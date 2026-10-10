using System.Text.Json;
using BanteraApi.Chat.Ai;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Gemini;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace BanteraApi.Tests;
public class AiLiveResumeSmokeTests
{
    [AiLiveFact]
    public async Task VoiceFollowupsAnswerLatestAndOnlySearchWhenExplicitlyAsked()
    {
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("BANTERA_AI_LIVE_CONFIG")!));
        var keys = config.RootElement.GetProperty("Gemini").GetProperty("ApiKeys").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")));
        using var provider = services.BuildServiceProvider();
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var options = Options.Create(new GeminiSettings { ApiKeys = keys });
        var health = new GeminiKeyHealthService(provider.GetRequiredService<IServiceScopeFactory>(), cache, options, NullLogger<GeminiKeyHealthService>.Instance);
        var live = new GeminiLiveService(options, health, NullLogger<GeminiLiveService>.Instance);
        var model = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_MODEL") ?? "gemini-3.8-live";
        var user = new User { Id = Guid.NewGuid(), Name = "Test learner", LearningLanguage = "en-NZ", NativeLanguage = "zh-CN" };
        var metadata = new AiClientMetadata(new("Pacific/Auckland", 780), null, true, DeviceWebSearch: true, ConversationId: Guid.NewGuid().ToString());
        var history = new List<AiContextTurn> {
            new("user", "Search the Asian food festival for lunch.", DateTimeOffset.UtcNow.AddMinutes(-3)),
            new("model", "I searched and found the festival. What food would you like?", DateTimeOffset.UtcNow.AddMinutes(-2))
        };
        string[] requests = ["What does skewer mean? Explain in one short sentence.",
            "What is broth? Explain in one short sentence.",
            "Please search the internet for the official New Zealand tourism website and tell me its name."];
        string[] expected = ["skewer", "broth", "New Zealand"];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        for (var i = 0; i < requests.Length; i++) {
            var background = history.Concat([new AiContextTurn("user", "Device capability: pictures use search_web images: prefix. Wait for current speech.")]).ToArray();
            using var socket = await live.ConnectAsync(model, user, true, background, timeout.Token, metadata);
            if (i > 0) Assert.True(AiLiveResumeCache.WasResumed(socket), "Follow-up must resume the provider session.");
            var input = new AiVoiceInput(); input.Add(new byte[16000]); input.Commit(metadata);
            await GeminiLiveService.UploadTranscriptAsync(socket, input, requests[i], timeout.Token, background);
            var searches = 0;
            var reply = await GeminiLiveService.ReadReplyAsync(socket, default, call => {
                if (call.GetProperty("name").GetString() == "search_web") searches++;
                return Task.FromResult<object>(new { results = new[] { new { title = "Official New Zealand tourism", url = "https://www.newzealand.com/", excerpt = "Tourism New Zealand's official travel website." } } });
            }, timeout.Token, requiresInteractionIdle: AiLiveModelPolicy.RequiresInteractionIdle(model));
            Assert.Contains(expected[i], reply.OutputText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("festival", reply.OutputText, StringComparison.OrdinalIgnoreCase);
            if (i < 2) Assert.Equal(0, searches); else Assert.True(searches > 0, "Explicit search must invoke the device tool.");
            using (var checkpoint = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token)) {
                checkpoint.CancelAfter(TimeSpan.FromSeconds(5));
                while (!AiLiveResumeCache.HasCheckpoint(socket)) { using var update = await GeminiLiveService.ReceiveJsonAsync(socket, checkpoint.Token); }
            }
            AiLiveResumeCache.Release(socket); socket.Abort();
            history.Add(new("user", requests[i], DateTimeOffset.UtcNow));
            // Simulate an image caption appended only by the app. It must not lose reuse.
            history.Add(new("model", reply.OutputText.Trim() + "\n[Shared image: skewer; source: https://example.com]", DateTimeOffset.UtcNow, ResumeText: reply.OutputText.Trim()));
            Console.WriteLine($"Live continuity round {i + 1}: resumed={i > 0}; searchCalls={searches}; audioBytes={reply.Pcm.Length}; latestTopicVerified=true.");
        }
    }

    [AiLiveFact]
    public async Task ProviderResumesWithoutReplayingHistory()
    {
        using var config=JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("BANTERA_AI_LIVE_CONFIG")!));
        var keys=config.RootElement.GetProperty("Gemini").GetProperty("ApiKeys").EnumerateArray().Select(x=>x.GetString()!).ToArray();
        var services=new ServiceCollection(); services.AddDbContext<AppDbContext>(o=>o.UseNpgsql(Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")));
        using var provider=services.BuildServiceProvider();
        using var cache=new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var options=Options.Create(new GeminiSettings {ApiKeys=keys});
        var health=new GeminiKeyHealthService(provider.GetRequiredService<IServiceScopeFactory>(),cache,options,NullLogger<GeminiKeyHealthService>.Instance);
        var live=new GeminiLiveService(options,health,NullLogger<GeminiLiveService>.Instance);
        var user=new User{Id=Guid.NewGuid(),Name="Test learner",LearningLanguage="en-NZ",NativeLanguage="en-US"};
        var metadata=new AiClientMetadata(new("Pacific/Auckland",780),null,true,ConversationId:Guid.NewGuid().ToString());
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(75));
        const string model="gemini-3.1-flash-live-preview";
        AiContextTurn[] historical = [new("user", "My favourite fruit is kiwi.", DateTimeOffset.UtcNow.AddDays(-1), "Pacific/Auckland", 780),
            new("model", "Got it.", DateTimeOffset.UtcNow.AddDays(-1), "Pacific/Auckland", 780)];
        using var first=await live.ConnectAsync(model,user,false,historical,timeout.Token,metadata);
        await GeminiLiveService.SendAsync(first,new {clientContent=new{turns=new[]{new{role="user",parts=new[]{new{text="What fruit did I tell you I like? Say just its name."}}}},turnComplete=true}},timeout.Token);
        var reply=await GeminiLiveService.ReadReplyAsync(first,default,null,timeout.Token);
        Assert.Contains("kiwi",reply.OutputText,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Historical message timing",reply.OutputText);
        Assert.DoesNotContain("Pacific/Auckland",reply.OutputText);
        using(var checkpoint=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token)) {
            checkpoint.CancelAfter(TimeSpan.FromSeconds(5));
            while(!AiLiveResumeCache.HasCheckpoint(first)) { using var update=await GeminiLiveService.ReceiveJsonAsync(first,checkpoint.Token); }
        }
        AiLiveResumeCache.Release(first); first.Abort();
        using var second=await live.ConnectAsync(model,user,false,[new("model",reply.OutputText.Trim(),DateTimeOffset.UtcNow)],timeout.Token,metadata);
        Assert.True(AiLiveResumeCache.WasResumed(second));
        await GeminiLiveService.SendAsync(second,new {clientContent=new{turns=new[]{new{role="user",parts=new[]{new{text="What fruit did I tell you I like? Say just its name."}}}},turnComplete=true}},timeout.Token);
        var recalled=await GeminiLiveService.ReadReplyAsync(second,default,null,timeout.Token);
        Assert.Contains("kiwi",recalled.OutputText,StringComparison.OrdinalIgnoreCase);
        AiLiveResumeCache.Release(second,false);
    }
}

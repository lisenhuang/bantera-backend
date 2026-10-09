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

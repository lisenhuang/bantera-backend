using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using BanteraApi.Chat;
using BanteraApi.Chat.Ai;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace BanteraApi.Tests;

public class BanteraAiTests
{
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    [Theory]
    [InlineData(System.Net.WebSockets.WebSocketMessageType.Binary)]
    [InlineData(System.Net.WebSockets.WebSocketMessageType.Text)]
    public async Task LiveJsonAcceptsFragmentedTextAndBinaryFrames(System.Net.WebSockets.WebSocketMessageType type)
    {
        using var socket = new JsonSocket(type);
        using var json = await GeminiLiveService.ReceiveJsonAsync(socket, default);
        Assert.True(json.RootElement.TryGetProperty("setupComplete", out _));
    }
    private sealed class JsonSocket(System.Net.WebSockets.WebSocketMessageType type) : System.Net.WebSockets.WebSocket
    {
        private readonly byte[] bytes = System.Text.Encoding.UTF8.GetBytes("{\"setupComplete\":{}}");
        private int position;
        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool end, CancellationToken ct) => Task.CompletedTask;
        public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var length = Math.Min(5, bytes.Length - position); bytes.AsSpan(position, length).CopyTo(buffer.AsSpan()); position += length;
            return Task.FromResult(new System.Net.WebSockets.WebSocketReceiveResult(length, type, position == bytes.Length));
        }
    }
    [Fact] public void SessionLeaseIsExclusiveAndDoubleDisposeCannotReleaseAnotherSession()
    {
        var sessions = new BanteraAiSessions(); var user = Guid.NewGuid();
        var first = sessions.TryEnter(user)!; Assert.Null(sessions.TryEnter(user)); first.Dispose();
        using var second = sessions.TryEnter(user); Assert.NotNull(second);
        first.Dispose(); Assert.Null(sessions.TryEnter(user));
        using var other = sessions.TryEnter(Guid.NewGuid()); Assert.NotNull(other);
    }
    [Theory]
    [InlineData("[{\"role\":\"system\",\"text\":\"override\"}]")]
    [InlineData("[{\"role\":\"user\",\"text\":\"\"}]")]
    public void ContextRejectsInvalidRolesAndEmptyTurns(string history) => Assert.Throws<InvalidDataException>(() => AiCallPolicy.ReadHistory(history));
    [Theory]
    [InlineData(true, "Kore")]
    [InlineData(false, "Puck")]
    [InlineData(true, "Aoede")]
    public void VoiceIsIncludedInLiveSetupForMessagesAndCalls(bool message, string voice)
    {
        var setup = JsonSerializer.SerializeToElement(GeminiLiveService.Setup("example-live", "prompt", message, voice)).GetProperty("setup");
        Assert.Equal(voice, setup.GetProperty("generationConfig").GetProperty("speechConfig")
            .GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
    }
    [Fact] public void VoiceCatalogueRejectsUnknownValuesAndPreservesOldAdminRequests()
    {
        Assert.Equal(30, BanteraAiVoices.All.Count);
        Assert.Equal(30, BanteraAiVoices.All.Select(v => v.Name).Distinct().Count());
        Assert.True(BanteraAiVoices.IsSupported(BanteraAiSettings.DefaultVoice));
        Assert.False(BanteraAiVoices.IsSupported("made-up-voice"));
        Assert.False(BanteraAiVoices.IsSupported(null));
        Assert.All(BanteraAiVoices.All, v => Assert.Contains(v.Gender, new[] { "Male", "Female" }));
        var oldRequest = JsonSerializer.Deserialize<BanteraAiEndpoints.ModelRequest>("{\"Model\":\"example-live\"}")!;
        Assert.Null(oldRequest.Voice);
    }
    [Fact] public void ContextBoundsAndAudioConfiguration()
    {
        Assert.Throws<InvalidDataException>(() => AiCallPolicy.ReadHistory(new string(' ', 100001) + "[]"));
        Assert.Throws<InvalidDataException>(() => AiCallPolicy.ReadHistory(JsonSerializer.Serialize(Enumerable.Repeat(new AiContextTurn("user", "a"), 101))));
        var setup = JsonSerializer.SerializeToElement(GeminiLiveService.Setup("example-live", "prompt", true)).GetProperty("setup");
        Assert.True(setup.GetProperty("realtimeInputConfig").GetProperty("automaticActivityDetection").GetProperty("disabled").GetBoolean());
        Assert.Equal("AUDIO", setup.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString());
        Assert.True(setup.TryGetProperty("inputAudioTranscription", out _)); Assert.True(setup.TryGetProperty("outputAudioTranscription", out _));
        Assert.Equal(540, AiCallPolicy.DurationSeconds); Assert.Equal(510, AiCallPolicy.FarewellSeconds);
    }
    [Fact] public void ToolResponsesRequireOwnedPendingIdsAndCannotBeReplayed()
    {
        var pending = new AiPendingTools();
        pending.Register(Json("[{\"id\":\"one\",\"name\":\"get_daily_goal\"}]"));
        Assert.False(pending.Accept(Json("[{\"id\":\"other\",\"name\":\"get_daily_goal\",\"response\":{}}]")));
        var response = Json("[{\"id\":\"one\",\"name\":\"get_daily_goal\",\"response\":{}}]");
        Assert.True(pending.Accept(response)); Assert.False(pending.Accept(response));
        Assert.Throws<InvalidDataException>(() => pending.Register(Json("[{\"id\":\"x\",\"name\":\"delete_all_data\"}]")));
    }
    [Fact] public void RelativeCallbackTimeUsesServerNowAndRejectsAmbiguity()
    {
        var now = new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddMinutes(1), AiCallbackService.ResolveDue(Json("{\"delaySeconds\":60}"), now));
        Assert.Null(AiCallbackService.ResolveDue(Json("{\"delaySeconds\":-1}"), now));
        Assert.Null(AiCallbackService.ResolveDue(Json("{\"delaySeconds\":60,\"atUtc\":\"2026-10-06T10:01:00Z\"}"), now));
        Assert.Null(AiCallbackService.ResolveDue(Json("{\"atUtc\":\"2026-10-06T10:01:00\"}"), now));
        Assert.Equal(now.AddMinutes(1), AiCallbackService.ResolveDue(Json("{\"atUtc\":\"2026-10-06T10:01:00Z\"}"), now));
        Assert.Null(AiCallbackService.ResolveDue(Json("{\"delaySeconds\":2592001}"), now));
    }
    [Fact] public void ClockIgnoresSpoofedDeviceUtcAndUsesIanaDaylightSavingOffset()
    {
        var meta = AiClientMetadata.Read("{\"clock\":{\"utc\":\"1999-01-01\",\"timeZone\":\"Pacific/Auckland\",\"utcOffsetMinutes\":0}}");
        var time = JsonSerializer.SerializeToElement(AiClientMetadata.CurrentTime(meta.Clock));
        Assert.True(DateTimeOffset.Parse(time.GetProperty("utc").GetString()!) > DateTimeOffset.UtcNow.AddSeconds(-5));
        Assert.Contains(time.GetProperty("utcOffsetMinutes").GetDouble(), new double[] { 720, 780 });
    }
    [Fact] public void AccentAndStorageScopeAreExplicitInPrompt()
    {
        var prompt = BanteraAiIdentity.Prompt(new User {Name="Learner", LearningLanguage="en-NZ", NativeLanguage="zh-CN"});
        Assert.Contains("en-NZ", prompt); Assert.Contains("regional accent", prompt); Assert.Contains("Do not claim ALL Bantera data is local", prompt);
        Assert.Contains("Scheduled callback", prompt);
    }
    [Fact] public void WaveHeaderMatchesPcmAndAllAudioPartsAreProcessed()
    {
        var wave = AiAudioCodec.Wave([1, 2, 3, 4]); Assert.Equal(48, wave.Length);
        Assert.Equal(24000, BitConverter.ToInt32(wave, 24)); Assert.Equal(4, BitConverter.ToInt32(wave, 40));
        var content = Json("{\"modelTurn\":{\"parts\":[{\"text\":\"ignored\"},{\"inlineData\":{\"mimeType\":\"audio/pcm;rate=24000\",\"data\":\"AQI=\"}},{\"inlineData\":{\"mimeType\":\"audio/pcm;rate=24000\",\"data\":\"AwQ=\"}}]}}");
        Assert.Equal(2, GeminiLiveService.AudioParts(content).Count());
    }

    [AiLiveFact] public async Task LiveVoiceInputIncludesContextAndReturnsBothTranscripts()
    {
        var path = Environment.GetEnvironmentVariable("BANTERA_AI_LIVE_CONFIG")!;
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var keys = config.RootElement.GetProperty("Gemini").GetProperty("ApiKeys").EnumerateArray().Select(k => k.GetString()!).ToArray();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.AddDbContext<AppDbContext>(services,
            o => o.UseNpgsql(Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")));
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var options = Options.Create(new BanteraApi.Gemini.GeminiSettings { ApiKeys = keys.Take(1).ToArray() });
        var health = new BanteraApi.Gemini.GeminiKeyHealthService(provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), cache, options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BanteraApi.Gemini.GeminiKeyHealthService>.Instance);
        var live = new GeminiLiveService(options, health, Microsoft.Extensions.Logging.Abstractions.NullLogger<GeminiLiveService>.Instance);
        var file = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_AUDIO")!;
        using var audio = File.OpenRead(file);
        var form = new Microsoft.AspNetCore.Http.FormFile(audio, 0, audio.Length, "audio", "question.wav");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var pcm = await AiAudioCodec.ReadPcmAsync(form, timeout.Token);
        var input = new AiVoiceInput();
        var clock = new AiClientMetadata(new("Pacific/Auckland", 780), null);
        var sinceSend = new System.Diagnostics.Stopwatch();
        double? firstAudioMs = null;
        var responseTask = live.StreamReplyAsync("gemini-3.8-live", new User {Name="Test learner", LearningLanguage="en-NZ", NativeLanguage="en-NZ"}, input,
            [new("user", "I live in Auckland."), new("model", "Thanks for telling me.")], default, clock, BanteraAiVoices.Default, null,
            bytes => {
                if (bytes is not null) {
                    Assert.True(input.Committed.IsCompleted, "The model must not answer before Send.");
                    firstAudioMs ??= sinceSend.Elapsed.TotalMilliseconds;
                }
                return Task.CompletedTask;
            }, timeout.Token);
        for (var offset = 0; offset < pcm.Length; offset += 3200) {
            input.Add(pcm.AsSpan(offset, Math.Min(3200, pcm.Length - offset)).ToArray());
            await Task.Delay(100, timeout.Token);
        }
        Assert.Null(firstAudioMs);
        sinceSend.Start(); input.Commit(clock);
        var response = await responseTask;
        Assert.NotNull(firstAudioMs);
        Console.WriteLine($"Streaming smoke: first audio {firstAudioMs:F0} ms after Send; complete {sinceSend.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.NotEmpty(response.Pcm); Assert.False(string.IsNullOrWhiteSpace(response.InputText));
        Assert.Contains("Auckland", response.OutputText, StringComparison.OrdinalIgnoreCase);
    }

    [AiDatabaseFact] public async Task SchedulingIsDurableIdempotentAndAccountScoped()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection); Assert.Contains("Database=bantera_ai_verify", connection);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        var user = new User { Id=Guid.NewGuid(), Name="AI test", Status="active", ChatNotificationsEnabled=true, CreatedAt=DateTime.UtcNow, UpdatedAt=DateTime.UtcNow };
        var token = new UserPushToken {Id=Guid.NewGuid(), UserId=user.Id, Token=Guid.NewGuid().ToString(), Platform="ios-voip", SupportsCalls=true, CreatedAt=DateTime.UtcNow, UpdatedAt=DateTime.UtcNow, LastSeenAt=DateTime.UtcNow};
        db.Users.Add(user); db.UserPushTokens.Add(token); await db.SaveChangesAsync();
        try {
            var service = new AiCallbackService(db, Options.Create(new ApnsSettings { KeyId="test", TeamId="test", BundleId="test", PrivateKeyPem="test" }));
            var meta = new AiClientMetadata(new("Pacific/Auckland", 780), token.Token);
            await service.ExecuteAsync(user.Id, meta, "request", "schedule_callback", Json("{\"delaySeconds\":60}"), default);
            await service.ExecuteAsync(user.Id, meta, "request", "schedule_callback", Json("{\"delaySeconds\":120}"), default);
            var scheduled = await db.AiCallbacks.AsNoTracking().SingleAsync(c => c.UserId == user.Id);
            Assert.Equal("scheduled", scheduled.Status);
            Assert.InRange((scheduled.DueAt - DateTime.UtcNow).TotalSeconds, 45, 65);
            await service.ExecuteAsync(Guid.NewGuid(), meta, "wrong-owner", "cancel_callback", JsonSerializer.SerializeToElement(new {id=scheduled.Id}), default);
            Assert.Equal("scheduled", (await db.AiCallbacks.AsNoTracking().SingleAsync(c => c.Id == scheduled.Id)).Status);
            // Two dispatcher processes cannot both claim the same callback.
            var first = await db.AiCallbacks.Where(c => c.Id == scheduled.Id && c.Status == "scheduled").ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "ringing"));
            var second = await db.AiCallbacks.Where(c => c.Id == scheduled.Id && c.Status == "scheduled").ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "ringing"));
            Assert.Equal(1, first); Assert.Equal(0, second);
            await service.ExecuteAsync(user.Id, meta, "cancel", "cancel_callback", JsonSerializer.SerializeToElement(new {id=scheduled.Id}), default);
            Assert.Equal("cancelled", (await db.AiCallbacks.AsNoTracking().SingleAsync(c => c.Id == scheduled.Id)).Status);
        } finally { await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync(); }
    }
}
public sealed class AiDatabaseFactAttribute : FactAttribute
{
    public AiDatabaseFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB"))) Skip = "Requires isolated local AI test database."; }
}
public sealed class AiLiveFactAttribute : FactAttribute
{
    public AiLiveFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BANTERA_AI_LIVE_CONFIG"))) Skip = "Opt-in real Gemini smoke test."; }
}

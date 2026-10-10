using System.Net.WebSockets;
using System.Text.Json;
using BanteraApi.Chat.Ai;
using BanteraApi.Database;
using BanteraApi.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BanteraApi.Tests;

public class AiChatDiagnosticsTests
{
    [Theory]
    [InlineData("callback_accept_failed")]
    [InlineData("callback_activation_timeout")]
    [InlineData("callback_audio_failed")]
    public void CallbackStartupDiagnosticsAcceptOnlyTechnicalMetadata(string code)
    {
        var report = new AiChatDiagnostics.ClientReport(Guid.NewGuid(), code, "callback", 0, 0, 0, false);
        Assert.True(AiChatDiagnostics.Valid(report));
        Assert.False(AiChatDiagnostics.Valid(report with { NativeCode = "private reminder text" }));
    }

    [Fact]
    public void OnlyBoundedTechnicalClientMetadataIsAccepted()
    {
        var report = new AiChatDiagnostics.ClientReport(Guid.NewGuid(), "socket_closed", "reply", 32000, 48000, 3000, true, 1006, "2.0.134+322");
        Assert.True(AiChatDiagnostics.Valid(report));
        Assert.False(AiChatDiagnostics.Valid(report with { RequestId = Guid.Empty }));
        Assert.False(AiChatDiagnostics.Valid(report with { Code = "secret transcript" }));
        Assert.False(AiChatDiagnostics.Valid(report with { Phase = "https://secret" }));
        Assert.False(AiChatDiagnostics.Valid(report with { AppVersion = "token=secret" }));
        Assert.False(AiChatDiagnostics.Valid(report with { OutputBytes = int.MaxValue }));
        Assert.False(AiChatDiagnostics.Valid(report with { ElapsedMs = -1 }));
        Assert.False(AiChatDiagnostics.Valid(report with { CloseCode = 999 }));
    }

    [Fact]
    public void FailureCategoriesDistinguishQuotaExpiryDeadlineAndDisconnect()
    {
        Assert.Equal("quota_unavailable", AiChatDiagnostics.Reason(new AiLiveQuotaUnavailableException()));
        Assert.Equal("session_expired", AiChatDiagnostics.Reason(new AiLiveSessionExpiredException()));
        Assert.Equal("response_stalled", AiChatDiagnostics.Reason(new AiLiveResponseTimeoutException()));
        Assert.Equal("request_deadline", AiChatDiagnostics.Reason(new OperationCanceledException()));
        Assert.Equal("client_disconnected", AiChatDiagnostics.Reason(new WebSocketException(), true));
        Assert.Equal("stream_failed", AiChatDiagnostics.Reason(new Exception("transcript/secret/key")));
    }

    [AiDatabaseFact]
    public async Task FailuresPersistWithCorrelationWithoutSensitiveExceptionContent()
    {
        var connection = Environment.GetEnvironmentVariable("BANTERA_AI_TEST_DB")!;
        Assert.Contains("Host=127.0.0.1", connection);
        Assert.Contains("Database=bantera_ai_verify", connection);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        services.AddSingleton<AiPipelineEventRecorder>();
        services.AddSingleton<AiChatDiagnostics>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<AiChatDiagnostics>();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        var user = Guid.NewGuid();
        var request = Guid.NewGuid();
        try {
            var error = new InvalidDataException("Live request failed.");
            error.Data["providerCode"] = 503;
            error.Data["privateMessage"] = "secret conversation and token";
            await service.FailureAsync(user, "en-NZ", "/ws/chat/ai/voice", request, error,
                new { outputBytes = 48000, committed = true }, 800, "test-model");
            await service.ClientAsync(user, new(request, "socket_closed", "reply", 32000, 48000, 900, true));
            var rows = await db.AiPipelineEvents.Where(e => e.UserId == user).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => {
                Assert.Contains(request.ToString(), row.DetailJson);
                Assert.DoesNotContain("secret", row.DetailJson);
            });
            var server = Assert.Single(rows, row => row.Stage == "ai_voice_server");
            Assert.Equal("provider_request_failed", server.Code);
            Assert.Equal(503, JsonDocument.Parse(server.DetailJson!).RootElement.GetProperty("providerCode").GetInt32());
            Assert.Equal("test-model", server.Model);
            var client = Assert.Single(rows, row => row.Stage == "ai_voice_client");
            Assert.Equal("socket_closed", client.Code);
            Assert.Equal("/api/chat/ai/diagnostics", client.Endpoint);
        } finally {
            await db.AiPipelineEvents.Where(e => e.UserId == user).ExecuteDeleteAsync();
        }
    }
}

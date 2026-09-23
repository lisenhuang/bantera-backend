using BanteraApi.Database;
using BanteraApi.Database.Entities;

namespace BanteraApi.Mcp;

/// <summary>
/// Writes the audit trail for MCP tool calls. Every write tool is logged (including
/// previews), as are tools that read personal data.
/// </summary>
public class McpAuditLogger(AppDbContext db, McpToolContext ctx, ILogger<McpAuditLogger> logger)
{
    public async Task<Guid> WriteAsync(
        string tool,
        object? args,
        string outcome,
        string? resultSummary = null,
        Guid? targetUserId = null,
        Guid? targetId = null,
        int durationMs = 0,
        CancellationToken ct = default)
    {
        try
        {
            var entry = new McpAuditLog
            {
                CreatedAt = DateTime.UtcNow,
                AdminUserId = ctx.AdminUserId,
                ClientId = ctx.ClientId,
                Tool = tool,
                ArgsJson = args is null ? "{}" : McpJson.Serialize(args),
                TargetUserId = targetUserId,
                TargetId = targetId,
                Outcome = outcome,
                ResultSummary = Truncate(resultSummary, 1000),
                DurationMs = durationMs,
            };

            db.McpAuditLogs.Add(entry);
            await db.SaveChangesAsync(ct);
            return entry.Id;
        }
        catch (Exception ex)
        {
            // Never fail the tool because auditing failed, but make it loud in the logs.
            logger.LogError(ex, "[MCP] Failed to write audit log for tool {Tool}", tool);
            return Guid.Empty;
        }
    }

    /// <summary>Updates a previously written "pending" row — used where the target is deleted mid-action.</summary>
    public async Task CompleteAsync(Guid auditId, string outcome, string? resultSummary, int durationMs, CancellationToken ct = default)
    {
        if (auditId == Guid.Empty) return;

        try
        {
            var entry = await db.McpAuditLogs.FindAsync([auditId], ct);
            if (entry is null) return;

            entry.Outcome = outcome;
            entry.ResultSummary = Truncate(resultSummary, 1000);
            entry.DurationMs = durationMs;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[MCP] Failed to complete audit log {AuditId}", auditId);
        }
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}

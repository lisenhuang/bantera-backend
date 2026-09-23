namespace BanteraApi.Database.Entities;

/// <summary>
/// Audit trail for admin actions taken through the MCP server. Written for every write
/// tool (including previews) and for tools that read personal data.
///
/// No foreign keys: the admin or the target user may later be deleted, and the audit
/// record must outlive them.
/// </summary>
public class McpAuditLog
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }

    public Guid AdminUserId { get; set; }

    /// <summary>The OAuth client that called the tool, e.g. Claude.</summary>
    public string? ClientId { get; set; }

    public string Tool { get; set; } = string.Empty;

    /// <summary>Tool arguments as JSON. Never contains secrets — tools do not take any.</summary>
    public string ArgsJson { get; set; } = "{}";

    public Guid? TargetUserId { get; set; }

    /// <summary>Video / message id when the action targets content rather than a user.</summary>
    public Guid? TargetId { get; set; }

    public string Outcome { get; set; } = McpAuditOutcomes.Ok;
    public string? ResultSummary { get; set; }
    public int DurationMs { get; set; }
}

public static class McpAuditOutcomes
{
    public const string Ok = "ok";
    public const string Error = "error";
    public const string Denied = "denied";
    public const string Preview = "preview";
    public const string Pending = "pending";
}

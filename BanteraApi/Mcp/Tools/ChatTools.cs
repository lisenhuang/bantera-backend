using System.ComponentModel;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace BanteraApi.Mcp.Tools;

/// <summary>
/// Chat analytics. Note that chat_messages rows are deleted seven days after sending, so
/// anything older comes from the durable per-day counters in the activity table.
/// </summary>
[McpServerToolType]
public sealed class ChatTools(AppDbContext db)
{
    private const string RetentionCaveat =
        "Voice messages are deleted 7 days after they are sent, so the messages table only covers the last week.";

    [McpServerTool(Name = "chat_overview")]
    [Description("""
        Chat health right now: threads by type, threads active in the last 7 days, messages
        currently stored, unique senders, group threads with their language and member counts,
        blocks, and average message length.
        """)]
    public async Task<string> ChatOverviewAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var since7 = now.AddDays(-7);

        var dm = await db.ChatThreads.CountAsync(t => t.Type == "dm", ct);
        var group = await db.ChatThreads.CountAsync(t => t.Type == "group", ct);
        var active7 = await db.ChatThreads.CountAsync(t => t.LastMessageAt != null && t.LastMessageAt >= since7, ct);

        var messagesStored = await db.ChatMessages.CountAsync(ct);
        var senders7 = await db.ChatMessages
            .Where(m => m.CreatedAt >= since7)
            .Select(m => m.SenderUserId).Distinct().CountAsync(ct);

        var avgMs = await db.ChatMessages.AverageAsync(m => (double?)m.DurationMs, ct);
        var blocks = await db.ChatBlocks.CountAsync(ct);

        var groups = await db.ChatThreads
            .Where(t => t.Type == "group")
            .Select(t => new
            {
                languageKey = t.LanguageKey,
                displayName = t.LanguageDisplayName,
                members = db.ChatThreadMemberships.Count(m => m.ThreadId == t.Id && m.DeletedAt == null),
                messagesLast7d = db.ChatMessages.Count(m => m.ThreadId == t.Id && m.CreatedAt >= since7),
                lastMessageAt = t.LastMessageAt,
            })
            .OrderByDescending(t => t.messagesLast7d)
            .Take(50)
            .ToListAsync(ct);

        return McpJson.Serialize(new
        {
            asOf = now,
            threads = new { dm, group, activeLast7d = active7 },
            messagesStored,
            sendersLast7d = senders7,
            avgDurationSec = avgMs is null ? 0 : Math.Round(avgMs.Value / 1000.0, 1),
            blocks,
            groups,
            caveats = new[] { RetentionCaveat },
        });
    }

    [McpServerTool(Name = "chat_timeseries")]
    [Description("""
        Voice messages sent per day. Uses the durable per-day counters recorded at send time,
        so it covers history beyond the 7-day message retention. Buckets before activity
        tracking went live will read zero.
        """)]
    public async Task<string> ChatTimeseriesAsync(
        [Description("Time window, e.g. '30d'. Default '30d'.")] string? period = null,
        CancellationToken ct = default)
    {
        var p = McpPeriod.Parse(period);
        var from = DateOnly.FromDateTime(p.FromUtc);
        var to = DateOnly.FromDateTime(p.ToUtc.AddDays(-1));

        var counters = await db.UserActivityDaily
            .Where(a => a.Date >= from && a.Date <= to && a.MessagesSent > 0)
            .GroupBy(a => a.Date)
            .Select(g => new { date = g.Key, messages = g.Sum(x => x.MessagesSent), senders = g.Count() })
            .ToListAsync(ct);

        var byDate = counters.ToDictionary(c => c.date);

        // Cross-check the last 7 days against the live messages table.
        var recentCutoff = DateTime.UtcNow.AddDays(-7);
        var recent = await db.ChatMessages
            .Where(m => m.CreatedAt >= recentCutoff)
            .GroupBy(m => new { m.CreatedAt.Date, ThreadType = db.ChatThreads.Where(t => t.Id == m.ThreadId).Select(t => t.Type).FirstOrDefault() })
            .Select(g => new { g.Key.Date, g.Key.ThreadType, N = g.Count() })
            .ToListAsync(ct);

        var recentByDate = recent
            .GroupBy(r => DateOnly.FromDateTime(r.Date))
            .ToDictionary(
                g => g.Key,
                g => new { dm = g.Where(x => x.ThreadType == "dm").Sum(x => x.N), grp = g.Where(x => x.ThreadType == "group").Sum(x => x.N) });

        var series = new List<object>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var counter = byDate.GetValueOrDefault(d);
            var live = recentByDate.GetValueOrDefault(d);
            series.Add(new
            {
                date = d,
                messages = counter?.messages ?? 0,
                senders = counter?.senders ?? 0,
                dm = live?.dm,
                group = live?.grp,
                source = counter is not null ? "counter" : (live is not null ? "messages" : "none"),
            });
        }

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            period = new { from, to, label = p.Label },
            series,
            caveats = new[]
            {
                "Per-day counters start when activity tracking went live; earlier days read zero even if messages were sent.",
                "The dm/group split is only available for the last 7 days, while the messages themselves still exist.",
            },
        });
    }

    [McpServerTool(Name = "chat_by_language")]
    [Description("""
        Voice messages from the last 7 days grouped by the language they were spoken in,
        using language families.
        """)]
    public async Task<string> ChatByLanguageAsync(
        [Description("How many language groups. 1-100, default 30.")] int limit = 30,
        CancellationToken ct = default)
    {
        limit = McpPaging.Clamp(limit);

        var rows = await db.ChatMessages
            .GroupBy(m => m.SpokenLanguageCode)
            .Select(g => new McpSql.CodeCountRow(g.Key, g.Count()))
            .ToListAsync(ct);

        var groups = McpLanguageGrouping.Group(rows.Select(r => (r.Code, r.Count)));

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            totalMessages = rows.Sum(r => r.Count),
            groups = groups.Take(limit),
            caveats = new[] { RetentionCaveat },
        });
    }

    [McpServerTool(Name = "message_lookup")]
    [Description("""
        Find voice messages from the last 7 days by id, thread, or sender. Returns metadata
        only — never the audio itself.
        """)]
    public async Task<string> MessageLookupAsync(
        [Description("Exact message id.")] Guid? messageId = null,
        [Description("Messages in this thread.")] Guid? threadId = null,
        [Description("Messages sent by this user.")] Guid? senderUserId = null,
        [Description("Filter by thread type: 'dm' or 'group'.")] string? threadType = null,
        [Description("Time window, e.g. '7d'. Default '7d'.")] string? period = null,
        [Description("How many messages. 1-50, default 20.")] int limit = 20,
        [Description("Rows to skip for paging.")] int offset = 0,
        CancellationToken ct = default)
    {
        var p = McpPeriod.Parse(period, "7d");
        limit = McpPaging.Clamp(limit, 50);
        offset = McpPaging.ClampOffset(offset);

        var query = db.ChatMessages.Where(m => m.CreatedAt >= p.FromUtc && m.CreatedAt < p.ToUtc);
        if (messageId is not null) query = query.Where(m => m.Id == messageId);
        if (threadId is not null) query = query.Where(m => m.ThreadId == threadId);
        if (senderUserId is not null) query = query.Where(m => m.SenderUserId == senderUserId);
        if (!string.IsNullOrWhiteSpace(threadType))
            query = query.Where(m => db.ChatThreads.Any(t => t.Id == m.ThreadId && t.Type == threadType));

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip(offset).Take(limit)
            .Select(m => new
            {
                id = m.Id,
                threadId = m.ThreadId,
                threadType = db.ChatThreads.Where(t => t.Id == m.ThreadId).Select(t => t.Type).FirstOrDefault(),
                groupLanguage = db.ChatThreads.Where(t => t.Id == m.ThreadId).Select(t => t.LanguageDisplayName).FirstOrDefault(),
                senderUserId = m.SenderUserId,
                senderName = db.Users.Where(u => u.Id == m.SenderUserId).Select(u => u.Name).FirstOrDefault(),
                languageCode = m.SpokenLanguageCode,
                durationSec = m.DurationMs / 1000,
                createdAt = m.CreatedAt,
                expiresAt = m.ExpiresAt,
                receipts = db.ChatMessageReceipts.Count(r => r.MessageId == m.Id),
            })
            .ToListAsync(ct);

        return McpJson.Serialize(new
        {
            asOf = DateTime.UtcNow,
            period = new { from = p.FromUtc, to = p.ToUtc, label = p.Label },
            total,
            items,
            caveats = new[] { RetentionCaveat },
        });
    }
}

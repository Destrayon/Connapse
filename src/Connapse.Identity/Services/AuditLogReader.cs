using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Identity.Services;

/// <summary>Reads the audit_logs table, newest first, with the acting user's email where the user still exists.</summary>
public sealed class AuditLogReader(IDbContextFactory<ConnapseIdentityDbContext> factory) : IAuditLogReader
{
    public async Task<AuditPage> ListAsync(AuditQuery query, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entries = db.AuditLogs.AsNoTracking();
        if (query.Action is { } action) entries = entries.Where(e => e.Action == action);
        if (query.UserId is { } userId) entries = entries.Where(e => e.UserId == userId);
        if (query.ResourceType is { } resourceType) entries = entries.Where(e => e.ResourceType == resourceType);
        if (query.ResourceId is { } resourceId) entries = entries.Where(e => e.ResourceId == resourceId);
        if (query.Since is { } since) entries = entries.Where(e => e.CreatedAt >= since);
        if (query.Until is { } until) entries = entries.Where(e => e.CreatedAt < until);

        // One more than the page, to know whether another follows. Id breaks ties between entries written in the same tick.
        var rows = await entries
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Skip(query.Skip).Take(query.Take + 1)
            .Select(e => new { Entry = e, Email = e.User == null ? null : e.User.Email })
            .ToListAsync(ct);

        var items = rows.Take(query.Take).Select(r => new AuditEntry(
            r.Entry.Id, r.Entry.CreatedAt, r.Entry.Action, r.Entry.UserId, r.Email,
            r.Entry.ResourceType, r.Entry.ResourceId, r.Entry.Details?.RootElement.Clone(), r.Entry.IpAddress)).ToList();
        return new AuditPage(items, rows.Count > query.Take);
    }
}

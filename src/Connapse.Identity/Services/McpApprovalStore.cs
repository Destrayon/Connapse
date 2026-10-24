using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Identity.Services;

/// <summary>How deciding on an approval request went.</summary>
public enum McpApprovalDecision
{
    Decided,
    NotFound,
    Forbidden,
    NotPending,
}

/// <summary>High-impact MCP calls waiting for a person, and the approvals that let them run once (#782).</summary>
public interface IMcpApprovalStore
{
    /// <summary>The pending request for this call, made now unless one is already waiting.</summary>
    Task<McpApprovalEntity> RequestAsync(string tokenKey, Guid ownerUserId, string toolName, string argumentsHash, string argumentsSummary, CancellationToken ct = default);

    /// <summary>Whether an approval for exactly this call is waiting; if so, it is used up and the call may run.</summary>
    Task<bool> TryConsumeAsync(string tokenKey, string toolName, string argumentsHash, CancellationToken ct = default);

    /// <summary>Requests still waiting: a person's own, or everyone's for an administrator.</summary>
    Task<IReadOnlyList<McpApprovalEntity>> ListPendingAsync(Guid userId, bool isAdmin, CancellationToken ct = default);

    /// <summary>Approves or denies a waiting request; only its owner or an administrator may.</summary>
    Task<McpApprovalDecision> DecideAsync(Guid id, Guid userId, bool isAdmin, bool approve, CancellationToken ct = default);
}

public sealed class McpApprovalStore(IDbContextFactory<ConnapseIdentityDbContext> contexts, TimeProvider clock) : IMcpApprovalStore
{
    /// <summary>How long a request waits for a person.</summary>
    public static readonly TimeSpan PendingFor = TimeSpan.FromDays(1);

    /// <summary>How long an approval may be used once given.</summary>
    public static readonly TimeSpan ApprovedFor = TimeSpan.FromMinutes(15);

    public async Task<McpApprovalEntity> RequestAsync(string tokenKey, Guid ownerUserId, string toolName, string argumentsHash, string argumentsSummary, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var waiting = await db.McpApprovals.FirstOrDefaultAsync(a => a.TokenKey == tokenKey && a.ToolName == toolName
            && a.ArgumentsHash == argumentsHash && a.Status == McpApprovalStatus.Pending && a.ExpiresAt > now, ct);
        if (waiting is not null)
            return waiting;

        var request = new McpApprovalEntity
        {
            Id = Guid.NewGuid(),
            TokenKey = tokenKey,
            OwnerUserId = ownerUserId,
            ToolName = toolName,
            ArgumentsHash = argumentsHash,
            ArgumentsSummary = argumentsSummary.Length <= 2048 ? argumentsSummary : argumentsSummary[..2045] + "...",
            Status = McpApprovalStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now + PendingFor,
        };
        db.McpApprovals.Add(request);
        await db.SaveChangesAsync(ct);
        return request;
    }

    public async Task<bool> TryConsumeAsync(string tokenKey, string toolName, string argumentsHash, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var approved = await db.McpApprovals
            .Where(a => a.TokenKey == tokenKey && a.ToolName == toolName && a.ArgumentsHash == argumentsHash
                && a.Status == McpApprovalStatus.Approved && a.ExpiresAt > now)
            .OrderBy(a => a.DecidedAt)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct);
        if (approved == Guid.Empty)
            return false;

        // Conditional on still being Approved, so two identical calls racing can't both use one approval.
        int used = await db.McpApprovals
            .Where(a => a.Id == approved && a.Status == McpApprovalStatus.Approved)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.Status, McpApprovalStatus.Used).SetProperty(a => a.UsedAt, now), ct);
        return used == 1;
    }

    public async Task<IReadOnlyList<McpApprovalEntity>> ListPendingAsync(Guid userId, bool isAdmin, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.McpApprovals.AsNoTracking()
            .Where(a => a.Status == McpApprovalStatus.Pending && a.ExpiresAt > now && (isAdmin || a.OwnerUserId == userId))
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<McpApprovalDecision> DecideAsync(Guid id, Guid userId, bool isAdmin, bool approve, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var request = await db.McpApprovals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (request is null || (!isAdmin && request.OwnerUserId != userId))
            return request is null ? McpApprovalDecision.NotFound : McpApprovalDecision.Forbidden;

        // Conditional on still being Pending, so an approve and a deny racing can't both land and the
        // later one overwrite the other.
        var status = approve ? McpApprovalStatus.Approved : McpApprovalStatus.Denied;
        var expiresAt = approve ? now + ApprovedFor : request.ExpiresAt;
        int decided = await db.McpApprovals
            .Where(a => a.Id == id && a.Status == McpApprovalStatus.Pending && a.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.DecidedAt, now)
                .SetProperty(a => a.DecidedByUserId, userId)
                .SetProperty(a => a.ExpiresAt, expiresAt), ct);
        return decided == 1 ? McpApprovalDecision.Decided : McpApprovalDecision.NotPending;
    }
}

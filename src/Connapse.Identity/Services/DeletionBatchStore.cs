using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Identity.Services;

/// <summary>How deciding on a deletion batch went.</summary>
public enum DeletionBatchDecision
{
    Decided,
    NotFound,
    Forbidden,
    NotPending,

    /// <summary>Approve was asked with no items of the batch selected.</summary>
    NothingSelected,
}

/// <summary>One item of a new batch, already checked against the token that asked.</summary>
public sealed record NewDeletionItem(DeletionItemKind Kind, Guid ContainerId, string Target, string Label, string Reason, string Fingerprint);

/// <summary>Batches of deletions an agent asked for, waiting for their owner, and what became of each item (CON-315).</summary>
public interface IDeletionBatchStore
{
    Task<DeletionBatchEntity> CreateAsync(string tokenKey, Guid ownerUserId, IReadOnlyList<NewDeletionItem> items, CancellationToken ct = default);

    /// <summary>One batch with its items, only if <paramref name="tokenKey"/> asked for it.</summary>
    Task<DeletionBatchEntity?> GetForTokenAsync(Guid id, string tokenKey, CancellationToken ct = default);

    /// <summary>One batch with its items, whoever asked.</summary>
    Task<DeletionBatchEntity?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Batches still waiting, with their items: a person's own, or everyone's for an administrator.</summary>
    Task<IReadOnlyList<DeletionBatchEntity>> ListPendingAsync(Guid userId, bool isAdmin, CancellationToken ct = default);

    /// <summary>
    /// Approves the items in <paramref name="approvedItemIds"/> (every item when null) and marks the rest not
    /// approved, or denies the whole batch. Only its owner may, and only while it is pending and unexpired. An
    /// approved batch is left <see cref="DeletionBatchStatus.Running"/> for the caller to carry out.
    /// </summary>
    Task<DeletionBatchDecision> DecideAsync(Guid id, Guid userId, bool approve, IReadOnlyCollection<Guid>? approvedItemIds, string? reason, CancellationToken ct = default);

    /// <summary>Records what became of one approved item.</summary>
    Task RecordAsync(Guid itemId, DeletionItemState state, string outcome, CancellationToken ct = default);

    /// <summary>Marks a running batch done once every approved item has an outcome.</summary>
    Task CompleteAsync(Guid id, CancellationToken ct = default);
}

public sealed class DeletionBatchStore(IDbContextFactory<ConnapseIdentityDbContext> contexts, TimeProvider clock) : IDeletionBatchStore
{
    /// <summary>How long a batch waits for its owner.</summary>
    public static readonly TimeSpan PendingFor = TimeSpan.FromDays(1);

    public async Task<DeletionBatchEntity> CreateAsync(string tokenKey, Guid ownerUserId, IReadOnlyList<NewDeletionItem> items, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(items.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(items.Count, DeletionBatchEntity.MaxItems);

        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var batch = new DeletionBatchEntity
        {
            Id = Guid.NewGuid(),
            TokenKey = tokenKey,
            OwnerUserId = ownerUserId,
            Status = DeletionBatchStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now + PendingFor,
        };
        for (int i = 0; i < items.Count; i++)
        {
            NewDeletionItem item = items[i];
            batch.Items.Add(new DeletionBatchItemEntity
            {
                Id = Guid.NewGuid(),
                BatchId = batch.Id,
                Position = i,
                Kind = item.Kind,
                ContainerId = item.ContainerId,
                Target = item.Target,
                Label = Clip(item.Label, DeletionBatchItemEntity.LabelMaxLength),
                Reason = Clip(item.Reason, DeletionBatchItemEntity.ReasonMaxLength),
                Fingerprint = item.Fingerprint,
                State = DeletionItemState.Pending,
            });
        }
        db.DeletionBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        return batch;
    }

    public async Task<DeletionBatchEntity?> GetForTokenAsync(Guid id, string tokenKey, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await WithItems(db).FirstOrDefaultAsync(b => b.Id == id && b.TokenKey == tokenKey, ct);
    }

    public async Task<DeletionBatchEntity?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await WithItems(db).FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    public async Task<IReadOnlyList<DeletionBatchEntity>> ListPendingAsync(Guid userId, bool isAdmin, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        return await WithItems(db)
            .Where(b => b.Status == DeletionBatchStatus.Pending && b.ExpiresAt > now && (isAdmin || b.OwnerUserId == userId))
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<DeletionBatchDecision> DecideAsync(
        Guid id, Guid userId, bool approve, IReadOnlyCollection<Guid>? approvedItemIds, string? reason, CancellationToken ct = default)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? null : Clip(reason.Trim(), McpApprovalEntity.DecisionReasonMaxLength);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var batch = await WithItems(db).FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null)
            return DeletionBatchDecision.NotFound;
        if (batch.OwnerUserId != userId)
            return DeletionBatchDecision.Forbidden;

        HashSet<Guid> approved = approve
            ? batch.Items.Select(i => i.Id).Where(i => approvedItemIds is null || approvedItemIds.Contains(i)).ToHashSet()
            : [];
        if (approve && approved.Count == 0)
            return DeletionBatchDecision.NothingSelected;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Conditional on still being Pending, so two decisions racing can't both land.
        var status = approve ? DeletionBatchStatus.Running : DeletionBatchStatus.Denied;
        int decided = await db.DeletionBatches
            .Where(b => b.Id == id && b.Status == DeletionBatchStatus.Pending && b.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set
                .SetProperty(b => b.Status, status)
                .SetProperty(b => b.DecidedAt, now)
                .SetProperty(b => b.DecidedByUserId, userId)
                .SetProperty(b => b.DecisionReason, reason)
                .SetProperty(b => b.CompletedAt, approve ? null : now), ct);
        if (decided != 1)
            return DeletionBatchDecision.NotPending;

        if (approve)
        {
            await db.DeletionBatchItems
                .Where(i => i.BatchId == id && approved.Contains(i.Id))
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.State, DeletionItemState.Approved), ct);
            await db.DeletionBatchItems
                .Where(i => i.BatchId == id && !approved.Contains(i.Id))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(i => i.State, DeletionItemState.NotApproved)
                    .SetProperty(i => i.Outcome, "The owner didn't approve this item; it was left alone.")
                    .SetProperty(i => i.FinishedAt, now), ct);
        }
        else
        {
            await db.DeletionBatchItems
                .Where(i => i.BatchId == id)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(i => i.State, DeletionItemState.Denied)
                    .SetProperty(i => i.Outcome, "The owner denied the batch; nothing was deleted.")
                    .SetProperty(i => i.FinishedAt, now), ct);
        }

        await tx.CommitAsync(ct);
        return DeletionBatchDecision.Decided;
    }

    public async Task RecordAsync(Guid itemId, DeletionItemState state, string outcome, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        string clipped = Clip(outcome, DeletionBatchItemEntity.OutcomeMaxLength);
        await db.DeletionBatchItems
            .Where(i => i.Id == itemId && i.State == DeletionItemState.Approved)
            .ExecuteUpdateAsync(set => set
                .SetProperty(i => i.State, state)
                .SetProperty(i => i.Outcome, clipped)
                .SetProperty(i => i.FinishedAt, now), ct);
    }

    public async Task CompleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        await db.DeletionBatches
            .Where(b => b.Id == id && b.Status == DeletionBatchStatus.Running
                && !db.DeletionBatchItems.Any(i => i.BatchId == id && i.State == DeletionItemState.Approved))
            .ExecuteUpdateAsync(set => set
                .SetProperty(b => b.Status, DeletionBatchStatus.Done)
                .SetProperty(b => b.CompletedAt, now), ct);
    }

    private static IQueryable<DeletionBatchEntity> WithItems(ConnapseIdentityDbContext db) =>
        db.DeletionBatches.AsNoTracking().Include(b => b.Items.OrderBy(i => i.Position));

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";
}

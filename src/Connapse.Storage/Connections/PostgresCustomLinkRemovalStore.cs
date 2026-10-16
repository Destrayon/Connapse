using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Connections;

public sealed class PostgresCustomLinkRemovalStore(IDbContextFactory<KnowledgeDbContext> factory) : ICustomLinkRemovalStore
{
    private const int MaxErrorLength = 1000;

    public async Task<int> EnqueueForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        List<Guid> providerIds = await context.CustomProviders.AsNoTracking()
            .Where(p => p.LinkUrl != null)
            .Select(p => p.Id)
            .ToListAsync(ct);
        HashSet<Guid> queued = [.. await context.CustomLinkRemovals.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Select(r => r.ProviderId)
            .ToListAsync(ct)];

        DateTime now = DateTime.UtcNow;
        List<CustomLinkRemovalEntity> added = providerIds.Where(id => !queued.Contains(id)).Select(id => new CustomLinkRemovalEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = id,
            UserId = userId,
            CreatedAt = now,
            NextAttemptAt = now,
        }).ToList();
        context.CustomLinkRemovals.AddRange(added);
        await context.SaveChangesAsync(ct);
        return added.Count;
    }

    public async Task CancelForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        await context.CustomLinkRemovals.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<CustomLinkRemoval>> DueAsync(DateTime now, int take, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        return await context.CustomLinkRemovals.AsNoTracking()
            .Where(r => r.NextAttemptAt <= now)
            .OrderBy(r => r.CreatedAt)
            .Take(take)
            .Select(r => new CustomLinkRemoval(r.Id, r.ProviderId, r.UserId, r.CreatedAt, r.Attempts, r.AlertedAt))
            .ToListAsync(ct);
    }

    public async Task CompleteAsync(Guid id, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        await context.CustomLinkRemovals.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task RetryLaterAsync(Guid id, DateTime nextAttemptAt, string error, bool alerted, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        string truncated = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        DateTime now = DateTime.UtcNow;
        await context.CustomLinkRemovals.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Attempts, r => r.Attempts + 1)
            .SetProperty(r => r.NextAttemptAt, nextAttemptAt)
            .SetProperty(r => r.LastError, truncated)
            .SetProperty(r => r.AlertedAt, r => alerted && r.AlertedAt == null ? now : r.AlertedAt), ct);
    }
}

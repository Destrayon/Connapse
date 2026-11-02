using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Documents;

public sealed class PostgresStoredFileRemovalStore(IDbContextFactory<KnowledgeDbContext> factory) : IStoredFileRemovalStore
{
    private const int MaxErrorLength = 1000;

    public async Task<IReadOnlyList<StoredFileRemoval>> DueAsync(DateTime now, int take, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        return await context.StoredFileRemovals.AsNoTracking()
            .Where(r => r.NextAttemptAt <= now)
            .OrderBy(r => r.CreatedAt)
            .Take(take)
            .Select(r => new StoredFileRemoval(r.Id, r.ContainerId, r.Path, r.CreatedAt, r.Attempts))
            .ToListAsync(ct);
    }

    public async Task CompleteAsync(Guid id, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        await context.StoredFileRemovals.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task RetryLaterAsync(Guid id, DateTime nextAttemptAt, string error, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        string truncated = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        await context.StoredFileRemovals.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Attempts, r => r.Attempts + 1)
            .SetProperty(r => r.NextAttemptAt, nextAttemptAt)
            .SetProperty(r => r.LastError, truncated), ct);
    }
}

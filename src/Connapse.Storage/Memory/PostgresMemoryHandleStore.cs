using System.Globalization;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Connapse.Storage.Memory;

/// <summary>
/// Handles in <c>memory_fact_handles</c>. New numbers are given out under a per-owner advisory lock, so two tools
/// showing facts at once never hand out the same number. Rows outlive a forgotten fact, so a number is never reused.
/// </summary>
public sealed class PostgresMemoryHandleStore(IDbContextFactory<KnowledgeDbContext> factory) : IMemoryHandleStore
{
    public async Task<IReadOnlyDictionary<Guid, int>> ShowAsync(Guid owner, IReadOnlyCollection<Guid> factIds, CancellationToken ct = default)
    {
        if (factIds.Count == 0)
            return new Dictionary<Guid, int>();
        Guid[] ids = [.. factIds.Distinct()];

        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        Dictionary<Guid, int> known = await Known(context, owner, ids, ct);
        if (known.Count == ids.Length)
            return known;

        await using (IDbContextTransaction tx = await context.Database.BeginTransactionAsync(ct))
        {
            string lockKey = $"memory_fact_handles:{owner}";
            await context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
            // Numbered in the order shown, after the owner's highest number so far.
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO memory_fact_handles (owner_id, handle, fact_id)
                SELECT {owner}, COALESCE((SELECT max(handle) FROM memory_fact_handles WHERE owner_id = {owner}), 0)
                       + row_number() OVER (ORDER BY t.ord), t.fact
                FROM unnest({ids}) WITH ORDINALITY AS t(fact, ord)
                WHERE NOT EXISTS (SELECT 1 FROM memory_fact_handles h WHERE h.owner_id = {owner} AND h.fact_id = t.fact)
                """, ct);
            await tx.CommitAsync(ct);
        }
        return await Known(context, owner, ids, ct);
    }

    public async Task<Guid?> ResolveAsync(Guid owner, string idOrHandle, CancellationToken ct = default)
    {
        string text = idOrHandle.Trim();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        if (TryHandle(text, out int handle))
            return await context.MemoryFactHandles.AsNoTracking()
                .Where(h => h.OwnerId == owner && h.Handle == handle)
                .Select(h => (Guid?)h.FactId).FirstOrDefaultAsync(ct);
        if (Guid.TryParse(text, out Guid id))
            return await context.MemoryFactHandles.AsNoTracking().AnyAsync(h => h.OwnerId == owner && h.FactId == id, ct) ? id : null;
        return null;
    }

    /// <summary>"f12" (either case) as 12.</summary>
    public static bool TryHandle(string text, out int handle)
    {
        handle = 0;
        return text.Length > 1 && (text[0] == 'f' || text[0] == 'F')
            && int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out handle)
            && handle > 0;
    }

    private static async Task<Dictionary<Guid, int>> Known(KnowledgeDbContext context, Guid owner, Guid[] ids, CancellationToken ct) =>
        await context.MemoryFactHandles.AsNoTracking()
            .Where(h => h.OwnerId == owner && ids.Contains(h.FactId))
            .ToDictionaryAsync(h => h.FactId, h => h.Handle, ct);
}

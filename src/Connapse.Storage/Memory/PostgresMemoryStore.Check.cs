using Connapse.Core;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Memory;

// Check-by dates (CON-340). A fact past its date is unconfirmed: still current, still recalled, only flagged, so an
// agent checks the real source before acting on it. A confirm moves the date on in place, the one change a fact takes
// besides being ended or superseded.
public sealed partial class PostgresMemoryStore
{
    /// <summary>
    /// Whether a fact is past its check-by date at <paramref name="now"/>: current (not superseded, not ended) with a
    /// date that has come. Kept in step with <see cref="Checked"/>, which says the same in SQL.
    /// </summary>
    internal static bool IsDue(DateTimeOffset? supersededAt, DateTimeOffset? validTo, DateTimeOffset? checkBy, DateTimeOffset now) =>
        supersededAt is null && (validTo is null || validTo > now) && checkBy is { } due && due <= now;

    /// <summary>The rows <paramref name="check"/> keeps: past their check-by date, every other one, or all of them.</summary>
    internal static IQueryable<MemoryFactEntity> Checked(IQueryable<MemoryFactEntity> rows, MemoryFactCheck check, DateTimeOffset now) => check switch
    {
        MemoryFactCheck.Due => rows.Where(f =>
            f.SupersededAt == null && (f.ValidTo == null || f.ValidTo > now) && f.CheckBy != null && f.CheckBy <= now),
        MemoryFactCheck.Fresh => rows.Where(f =>
            f.SupersededAt != null || (f.ValidTo != null && f.ValidTo <= now) || f.CheckBy == null || f.CheckBy > now),
        _ => rows,
    };

    /// <summary>
    /// The check-by date and period for a fact on <paramref name="attribute"/> saved at <paramref name="now"/>: the
    /// ones <paramref name="check"/> asks for, else the space's default for the attribute, else
    /// <paramref name="fallbackDays"/>, else none.
    /// </summary>
    private static async Task<(DateTimeOffset? CheckBy, int? Days)> CheckDatesAsync(
        KnowledgeDbContext context, Guid containerId, string attribute, MemoryCheckBy? check, int? fallbackDays,
        DateTimeOffset now, CancellationToken ct)
    {
        if (check is not null)
            return check.From(now);
        string key = CheckKey(attribute);
        int? days = await context.MemoryCheckDefaults.AsNoTracking()
            .Where(d => d.ContainerId == containerId && d.Attribute == key)
            .Select(d => (int?)d.Days)
            .FirstOrDefaultAsync(ct) ?? fallbackDays;
        return days is { } d ? (now.AddDays(d), d) : (null, null);
    }

    /// <summary>How an attribute is kept in <c>memory_check_defaults</c>: trimmed and lower-cased, so any case matches.</summary>
    internal static string CheckKey(string attribute) => attribute.Trim().ToLowerInvariant();

    public async Task<IReadOnlyDictionary<string, int>> CheckDefaultsAsync(Guid containerId, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context.MemoryCheckDefaults.AsNoTracking()
            .Where(d => d.ContainerId == containerId)
            .OrderBy(d => d.Attribute)
            .ToDictionaryAsync(d => d.Attribute, d => d.Days, ct);
    }

    public async Task SetCheckDefaultsAsync(Guid containerId, IReadOnlyDictionary<string, int> defaults, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await context.MemoryCheckDefaults.Where(d => d.ContainerId == containerId).ExecuteDeleteAsync(ct);
        foreach ((string attribute, int days) in defaults)
            context.MemoryCheckDefaults.Add(new MemoryCheckDefaultEntity { ContainerId = containerId, Attribute = CheckKey(attribute), Days = days });
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<MemoryWriteResult> ConfirmAsync(
        Guid factId, MemoryCheckBy? check, Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>> allow,
        CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        if (await LockedChainAsync(context, factId, ct) is not { } chain)
            return MemoryWriteResult.Failed($"Fact '{factId}' not found.", MemoryWriteFailure.NotFound);
        MemoryFactEntity row = chain.Single(f => f.Id == factId);
        if (await allow([.. chain.Select(ToFact)]) is { } refused)
            return refused;
        DateTimeOffset now = clock.GetUtcNow();
        if (row.SupersededAt is not null)
            return MemoryWriteResult.Failed($"Fact '{factId}' was corrected; confirm its replacement instead.");
        if (row.ValidTo is { } ended && ended <= now)
            return MemoryWriteResult.Failed($"Fact '{factId}' ended on {ended:u}, so there is nothing to confirm.");

        // A period asked for replaces the fact's own; without one, the fact's own starts again from now.
        (row.CheckBy, row.CheckAfterDays) = check is not null ? check.From(now)
            : row.CheckAfterDays is { } days ? (now.AddDays(days), days)
            : (null, null);
        row.ConfirmedAt = now;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new MemoryWriteResult(ToFact(row), null, null) { Confirmed = true };
    }
}

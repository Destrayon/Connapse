using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Memory;

/// <summary>
/// Agent memory in two plain tables. A fact has two time axes: when it was true (valid_from, valid_to) and when
/// memory held it (recorded_at, superseded_at); a null end is open. A correction ends or supersedes the old row and
/// inserts the new one in one transaction, with the old row locked so two corrections cannot both replace it.
/// </summary>
public sealed partial class PostgresMemoryStore(IDbContextFactory<KnowledgeDbContext> contextFactory, TimeProvider clock) : IMemoryStore
{
    public async Task<MemoryFact> AddAsync(Guid containerId, NewMemoryFact fact, MemoryCaller author, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        MemoryFactEntity row = NewRow(containerId, fact, author, chainId: null, replaces: null);
        context.MemoryFacts.Add(row);
        await context.SaveChangesAsync(ct);
        return ToFact(row);
    }

    public async Task<MemoryWriteResult> CorrectAsync(
        Guid containerId, Guid factId, MemoryCorrection kind, NewMemoryFact replacement, MemoryCaller author, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        MemoryFactEntity? old = await context.MemoryFacts
            .FromSql($"SELECT * FROM memory_facts WHERE id = {factId} AND container_id = {containerId} FOR UPDATE")
            .Include(f => f.Sources)
            .SingleOrDefaultAsync(ct);
        if (old is null)
            return MemoryWriteResult.Failed($"Fact '{factId}' not found.");
        if (old.SupersededAt is not null)
            return MemoryWriteResult.Failed($"Fact '{factId}' was already corrected; correct its replacement instead.");

        NewMemoryFact next = replacement;
        if (kind == MemoryCorrection.Changed)
        {
            if (old.ValidTo is not null)
                return MemoryWriteResult.Failed($"Fact '{factId}' already ended on {old.ValidTo:u}.");
            if (replacement.ValidFrom is not { } from)
                return MemoryWriteResult.Failed("A change needs 'validFrom', the date the new value took effect.");
            if (old.ValidFrom is { } oldFrom && from <= oldFrom)
                return MemoryWriteResult.Failed($"'validFrom' must be after the old fact's start ({oldFrom:u}).");
            old.ValidTo = from;
        }
        else
        {
            old.SupersededAt = clock.GetUtcNow();
            next = replacement with
            {
                ValidFrom = replacement.ValidFrom ?? old.ValidFrom,
                ValidTo = replacement.ValidTo ?? old.ValidTo,
            };
        }

        MemoryFactEntity row = NewRow(containerId, next, author, old.ChainId, old.Id);
        context.MemoryFacts.Add(row);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new MemoryWriteResult(ToFact(row), ToFact(old), null);
    }

    public async Task<int?> ForgetAsync(
        Guid containerId, Guid factId, Func<IReadOnlyList<MemoryFact>, Task<bool>> allow, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        Guid? chain = await context.MemoryFacts
            .Where(f => f.Id == factId && f.ContainerId == containerId)
            .Select(f => (Guid?)f.ChainId)
            .SingleOrDefaultAsync(ct);
        if (chain is null)
            return null;

        // Locked, so a correction cannot add a version between the check and the delete.
        List<MemoryFactEntity> rows = await context.MemoryFacts
            .FromSql($"SELECT * FROM memory_facts WHERE chain_id = {chain.Value} AND container_id = {containerId} FOR UPDATE")
            .AsNoTracking()
            .Include(f => f.Sources)
            .ToListAsync(ct);
        if (!await allow([.. rows.Select(ToFact)]))
            return null;

        // Sources go with their facts (cascade).
        int deleted = await context.MemoryFacts.Where(f => f.ChainId == chain && f.ContainerId == containerId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }

    public async Task<MemoryFact?> GetAsync(Guid containerId, Guid factId, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        MemoryFactEntity? row = await context.MemoryFacts.AsNoTracking().Include(f => f.Sources)
            .SingleOrDefaultAsync(f => f.Id == factId && f.ContainerId == containerId, ct);
        return row is null ? null : ToFact(row);
    }

    public async Task<IReadOnlyList<MemoryFact>> QueryAsync(MemoryQuery query, int skip, int take, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        IQueryable<MemoryFactEntity> rows = context.MemoryFacts.AsNoTracking().Where(f => f.ContainerId == query.ContainerId);

        if (!string.IsNullOrWhiteSpace(query.Subject))
        {
            string subject = query.Subject.Trim().ToLowerInvariant();
            rows = rows.Where(f => f.Subject.ToLower() == subject);
        }
        if (!string.IsNullOrWhiteSpace(query.Attribute))
        {
            string attribute = query.Attribute.Trim().ToLowerInvariant();
            rows = rows.Where(f => f.Attribute.ToLower() == attribute);
        }

        if (query.ChangedFrom is not null || query.ChangedTo is not null)
        {
            DateTimeOffset from = query.ChangedFrom ?? DateTimeOffset.MinValue;
            DateTimeOffset to = query.ChangedTo ?? DateTimeOffset.MaxValue;
            // Corrected facts included: a window's history shows what was believed and replaced too.
            rows = rows.Where(f => (f.ValidFrom >= from && f.ValidFrom <= to) || (f.ValidTo >= from && f.ValidTo <= to));
        }
        else if (!query.History)
        {
            DateTimeOffset at = query.AsOf ?? clock.GetUtcNow();
            rows = rows.Where(f => f.SupersededAt == null
                && (f.ValidFrom == null || f.ValidFrom <= at)
                && (f.ValidTo == null || f.ValidTo > at));
        }

        string? terms = TsQuery(query.Text);
        IQueryable<MemoryFactEntity> ordered;
        if (terms is null)
        {
            ordered = query.ChangedFrom is not null || query.ChangedTo is not null || query.History
                ? rows.OrderBy(f => f.ValidFrom).ThenBy(f => f.RecordedAt).ThenBy(f => f.Id)
                : rows.OrderByDescending(f => f.RecordedAt).ThenBy(f => f.Id);
        }
        else
        {
            ordered = rows
                .Where(f => f.SearchVector.Matches(EF.Functions.ToTsQuery("english", terms)))
                .OrderByDescending(f => f.SearchVector.Rank(EF.Functions.ToTsQuery("english", terms)))
                .ThenByDescending(f => f.ValidFrom)
                .ThenByDescending(f => f.RecordedAt)
                .ThenBy(f => f.Id);
        }

        List<MemoryFactEntity> page = await ordered.Skip(skip).Take(take).Include(f => f.Sources).ToListAsync(ct);
        return [.. page.Select(ToFact)];
    }

    public async Task<IReadOnlyDictionary<string, string>> DocumentTitlesAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default)
    {
        Guid[] ids = [.. documentIds.Select(id => Guid.TryParse(id, out Guid g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct()];
        if (ids.Length == 0)
            return new Dictionary<string, string>();
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context.Documents.AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id.ToString(), d => string.IsNullOrEmpty(d.Path) ? d.FileName : d.Path, ct);
    }

    /// <summary>
    /// The words of <paramref name="text"/> OR-ed into a tsquery, so a question matches the facts that share any of
    /// its words and ranks those sharing more first. Only letters and digits reach the query, so nothing in the text
    /// is read as tsquery syntax. Null when there are no words.
    /// </summary>
    internal static string? TsQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string[] words = [.. Words().Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().Take(32)];
        return words.Length == 0 ? null : string.Join(" | ", words);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();

    private MemoryFactEntity NewRow(Guid containerId, NewMemoryFact fact, MemoryCaller author, Guid? chainId, Guid? replaces)
    {
        Guid id = Guid.CreateVersion7();
        return new MemoryFactEntity
        {
            Id = id,
            ChainId = chainId ?? id,
            ContainerId = containerId,
            Subject = fact.Subject,
            Attribute = fact.Attribute,
            Value = fact.Value,
            ValidFrom = fact.ValidFrom,
            ValidTo = fact.ValidTo,
            RecordedAt = clock.GetUtcNow(),
            ReplacesId = replaces,
            Reason = fact.Reason,
            AuthorUserId = author.UserId,
            AuthorAgentId = author.AgentId,
            AuthorName = author.Name,
            Sources = [.. fact.Sources.Select((s, i) => new MemoryFactSourceEntity { FactId = id, Ordinal = i, Kind = (short)s.Kind, Ref = s.Ref })],
        };
    }

    private static MemoryFact ToFact(MemoryFactEntity row) => new(
        row.Id, row.ChainId, row.ContainerId, row.Subject, row.Attribute, row.Value,
        row.ValidFrom, row.ValidTo, row.RecordedAt, row.SupersededAt, row.ReplacesId, row.Reason,
        new MemoryCaller(row.AuthorUserId, row.AuthorAgentId, row.AuthorName),
        [.. row.Sources.OrderBy(s => s.Ordinal).Select(s => new MemorySource((MemorySourceKind)s.Kind, s.Ref))]);
}

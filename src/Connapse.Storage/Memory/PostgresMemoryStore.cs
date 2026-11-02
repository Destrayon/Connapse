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

        // Every correction of a chain first locks its first fact (whose id is the chain's), so they run one at a time. A
        // wrong correction is checked against the other versions, so they are read only once that lock is held, in a
        // statement of their own: under read committed, a locking read that waited sees the rows it locked as updated
        // but not versions the other correction inserted.
        List<Guid> locked = await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM memory_facts
            WHERE container_id = {containerId}
              AND id = (SELECT chain_id FROM memory_facts WHERE id = {factId} AND container_id = {containerId})
            FOR UPDATE
            """).ToListAsync(ct);
        if (locked.Count == 0)
            return MemoryWriteResult.Failed($"Fact '{factId}' not found.", MemoryWriteFailure.NotFound);
        Guid chainId = locked[0];
        List<MemoryFactEntity> chain = await context.MemoryFacts
            .FromSql($"SELECT * FROM memory_facts WHERE chain_id = {chainId} AND container_id = {containerId} FOR UPDATE")
            .Include(f => f.Sources)
            .ToListAsync(ct);
        MemoryFactEntity? old = chain.SingleOrDefault(f => f.Id == factId);
        if (old is null)
            return MemoryWriteResult.Failed($"Fact '{factId}' not found.", MemoryWriteFailure.NotFound);
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
            next = replacement with
            {
                ValidFrom = replacement.ValidFrom ?? old.ValidFrom,
                ValidTo = replacement.ValidTo ?? old.ValidTo,
            };
            if (WrongWindowError(old, next, chain) is { } error)
                return MemoryWriteResult.Failed(error);
            old.SupersededAt = clock.GetUtcNow();
        }

        MemoryFactEntity row = NewRow(containerId, next, author, old.ChainId, old.Id);
        context.MemoryFacts.Add(row);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new MemoryWriteResult(ToFact(row), ToFact(old), null);
    }

    /// <summary>
    /// Why <paramref name="next"/> cannot replace <paramref name="old"/> as a wrong correction, or null when it can. The
    /// versions of a chain memory still holds never overlap and meet where one ends and the next starts, so recall on any
    /// date they cover finds exactly one. The replacement's window must hold a moment, and a date where another version
    /// meets the old one stays put: moving it would leave dates with no version or with two.
    /// </summary>
    private static string? WrongWindowError(MemoryFactEntity old, NewMemoryFact next, IEnumerable<MemoryFactEntity> chain)
    {
        // Only reachable with one date inherited from the old fact; the service already refused two given dates out of order.
        if (next.ValidFrom is { } from && next.ValidTo is { } to && to <= from)
            return $"The new window would run from {from:u} to {to:u}, the old fact's other date; pass both 'validFrom' and 'validTo'.";

        List<MemoryFactEntity> held = [.. chain.Where(f => f.Id != old.Id && f.SupersededAt is null)];
        if (old.ValidFrom is { } oldFrom && next.ValidFrom != oldFrom && held.Any(f => f.ValidTo == oldFrom))
            return $"The version before this one ends on {oldFrom:u}, where this one starts, so a wrong correction must keep that 'validFrom'.";
        if (old.ValidTo is { } oldTo && next.ValidTo != oldTo && held.Any(f => f.ValidFrom == oldTo))
            return $"The version after this one starts on {oldTo:u}, where this one ends, so a wrong correction must keep that 'validTo'.";
        return null;
    }

    public async Task<int?> ForgetAsync(
        Guid containerId, Guid factId, Func<IReadOnlyList<MemoryFact>, Task<bool>> allow, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
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

            // Only the versions allow saw. Under read committed the lock above finds the rows committed when it began,
            // so a version a correction committed while it waited for that correction's lock is not among them, though
            // a delete by chain would find it. Sources go with their facts (cascade).
            Guid[] checkedIds = [.. rows.Select(r => r.Id)];
            int deleted = await context.MemoryFacts.Where(f => checkedIds.Contains(f.Id)).ExecuteDeleteAsync(ct);

            // A version left over was never checked: undo and start again, so it is. Past the last attempt, refuse.
            if (!await context.MemoryFacts.AnyAsync(f => f.ChainId == chain && f.ContainerId == containerId, ct))
            {
                await transaction.CommitAsync(ct);
                return deleted;
            }
            await transaction.RollbackAsync(ct);
            if (attempt == MaxForgetAttempts)
                return null;
        }
    }

    /// <summary>How many times a forget starts again when the chain grew under it, before it refuses.</summary>
    private const int MaxForgetAttempts = 3;

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
            string subject = ExactILike(query.Subject.Trim());
            rows = rows.Where(f => EF.Functions.ILike(f.Subject, subject, "\\"));
        }
        if (!string.IsNullOrWhiteSpace(query.Attribute))
        {
            string attribute = ExactILike(query.Attribute.Trim());
            rows = rows.Where(f => EF.Functions.ILike(f.Attribute, attribute, "\\"));
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

        string? terms = TsQuery(await DistinctiveWordsAsync(context, query.ContainerId, QueryWords(query.Text), ct));
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
    // An ILIKE pattern matching exactly this text in any case: its wildcards and the escape character are escaped.
    private static string ExactILike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    internal static string? TsQuery(string? text) => TsQuery(QueryWords(text));

    private static string? TsQuery(IReadOnlyList<string> words) => words.Count == 0 ? null : string.Join(" | ", words);

    internal static IReadOnlyList<string> QueryWords(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [.. Words().Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().Take(32)];

    /// <summary>
    /// The words that tell this container's facts apart. ts_rank has no inverse document frequency, so a word in most
    /// facts (the project every fact is about, a ticket prefix) would outrank the one word that picks the fact; such
    /// words are left out when the container has enough facts to tell, unless that would leave nothing.
    /// </summary>
    private static async Task<IReadOnlyList<string>> DistinctiveWordsAsync(
        KnowledgeDbContext context, Guid containerId, IReadOnlyList<string> words, CancellationToken ct)
    {
        if (words.Count < 2)
            return words;
        string[] all = [.. words];
        List<WordCount> counts = await context.Database.SqlQuery<WordCount>($"""
            SELECT w AS "Word",
                   (SELECT count(*)::int FROM memory_facts f
                     WHERE f.container_id = {containerId} AND f.search_vector @@ to_tsquery('english', w)) AS "Matches",
                   (SELECT count(*)::int FROM memory_facts f WHERE f.container_id = {containerId}) AS "Facts"
            FROM unnest({all}) AS w
            """).ToListAsync(ct);
        if (counts.Count == 0 || counts[0].Facts < MinFactsToPrune)
            return words;
        HashSet<string> common = [.. counts.Where(c => c.Matches * 2 > c.Facts).Select(c => c.Word)];
        // A kept word must match something, or a question of stop words and common words would match nothing at all.
        List<string> kept = [.. words.Where(w => !common.Contains(w))];
        return counts.Any(c => c.Matches > 0 && !common.Contains(c.Word)) ? kept : words;
    }

    /// <summary>Below this many facts a word's share says little about how common it is.</summary>
    private const int MinFactsToPrune = 8;

    private sealed class WordCount
    {
        public string Word { get; init; } = "";
        public int Matches { get; init; }
        public int Facts { get; init; }
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

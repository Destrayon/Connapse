using System.Linq.Expressions;
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
        Guid factId, MemoryCorrection kind, NewMemoryFact replacement, MemoryCaller author,
        Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>> check, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        if (await LockedChainAsync(context, factId, ct) is not { } chain)
            return MemoryWriteResult.Failed($"Fact '{factId}' not found.", MemoryWriteFailure.NotFound);
        MemoryFactEntity old = chain.Single(f => f.Id == factId);
        if (await check([.. chain.Select(ToFact)]) is { } refused)
            return refused;
        if (old.SupersededAt is not null)
            return MemoryWriteResult.Failed($"Fact '{factId}' was already corrected; correct its replacement instead.");

        // A correction keeps the scope the fact has now, under the lock a scope change also takes.
        NewMemoryFact next = replacement with { Scope = (MemoryScope)old.Scope };
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
            next = next with
            {
                ValidFrom = replacement.ValidFrom ?? old.ValidFrom,
                ValidTo = replacement.ValidTo ?? old.ValidTo,
            };
            if (WrongWindowError(old, next, chain) is { } error)
                return MemoryWriteResult.Failed(error);
            old.SupersededAt = clock.GetUtcNow();
        }

        // The new version joins the chain where it is: a chain never spans two containers.
        MemoryFactEntity row = NewRow(old.ContainerId, next, author, old.ChainId, old.Id);
        context.MemoryFacts.Add(row);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new MemoryWriteResult(ToFact(row), ToFact(old), null);
    }

    /// <summary>
    /// Every version of <paramref name="factId"/>'s chain, locked, or null when there is no such fact. Every
    /// write to a chain first locks its first fact (whose id is the chain's), so they run one at a time. A write is
    /// checked against the other versions, so they are read only once that lock is held, in a statement of their own:
    /// under read committed, a locking read that waited sees the rows it locked as updated but not versions the other
    /// correction inserted.
    /// </summary>
    private static async Task<List<MemoryFactEntity>?> LockedChainAsync(
        KnowledgeDbContext context, Guid factId, CancellationToken ct)
    {
        List<Guid> locked = await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM memory_facts
            WHERE id = (SELECT chain_id FROM memory_facts WHERE id = {factId})
            FOR UPDATE
            """).ToListAsync(ct);
        if (locked.Count == 0)
            return null;
        Guid chainId = locked[0];
        List<MemoryFactEntity> chain = await context.MemoryFacts
            .FromSql($"SELECT * FROM memory_facts WHERE chain_id = {chainId} FOR UPDATE")
            .Include(f => f.Sources)
            .ToListAsync(ct);
        // Forgotten after the first lookup: the chain's rows went with it.
        return chain.Any(f => f.Id == factId) ? chain : null;
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
        Guid factId, Func<IReadOnlyList<MemoryFact>, Task<bool>> allow, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            Guid? chain = await context.MemoryFacts
                .Where(f => f.Id == factId)
                .Select(f => (Guid?)f.ChainId)
                .SingleOrDefaultAsync(ct);
            if (chain is null)
                return null;

            // Locked, so a correction cannot add a version between the check and the delete.
            List<MemoryFactEntity> rows = await context.MemoryFacts
                .FromSql($"SELECT * FROM memory_facts WHERE chain_id = {chain.Value} FOR UPDATE")
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
            if (!await context.MemoryFacts.AnyAsync(f => f.ChainId == chain, ct))
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

    public async Task<MemoryFact?> GetAsync(Guid factId, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        MemoryFactEntity? row = await context.MemoryFacts.AsNoTracking().Include(f => f.Sources)
            .SingleOrDefaultAsync(f => f.Id == factId, ct);
        return row is null ? null : ToFact(row);
    }

    public Task<IReadOnlyList<MemoryFact>> QueryAsync(MemoryQuery query, int skip, int take, CancellationToken ct = default) =>
        QueryAsync(query, null, skip, take, ct);

    public Task<IReadOnlyList<MemoryFact>> VisibleAsync(MemoryQuery query, MemoryViewer viewer, int take, CancellationToken ct = default) =>
        QueryAsync(query, viewer, 0, take, ct);

    private async Task<IReadOnlyList<MemoryFact>> QueryAsync(MemoryQuery query, MemoryViewer? viewer, int skip, int take, CancellationToken ct)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        Guid[] containers = [.. query.Containers.Distinct()];
        IQueryable<MemoryFactEntity> rows = containers.Length == 1
            ? context.MemoryFacts.AsNoTracking().Where(f => f.ContainerId == containers[0])
            : context.MemoryFacts.AsNoTracking().Where(f => containers.Contains(f.ContainerId));
        if (viewer is not null)
            rows = VisibleTo(rows, viewer);

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

        bool history = query.History || query.ChainId is not null;
        if (query.ChainId is { } chainId)
            rows = rows.Where(f => f.ChainId == chainId);

        if (query.ChangedFrom is not null || query.ChangedTo is not null)
        {
            DateTimeOffset from = query.ChangedFrom ?? DateTimeOffset.MinValue;
            DateTimeOffset to = query.ChangedTo ?? DateTimeOffset.MaxValue;
            // Corrected facts included: a window's history shows what was believed and replaced too.
            rows = rows.Where(f => (f.ValidFrom >= from && f.ValidFrom <= to) || (f.ValidTo >= from && f.ValidTo <= to));
        }
        else if (!history)
        {
            DateTimeOffset at = query.AsOf ?? clock.GetUtcNow();
            rows = rows.Where(f => f.SupersededAt == null
                && (f.ValidFrom == null || f.ValidFrom <= at)
                && (f.ValidTo == null || f.ValidTo > at));
        }

        IReadOnlyList<string> words = QueryWords(query.Text);
        if (containers.Length > 1 && words.Count > 0)
            return await RankedAcrossAsync(context, rows, containers, words, skip, take, ct);

        string? terms = TsQuery(await DistinctiveWordsAsync(context, containers[0], words, ct));
        IQueryable<MemoryFactEntity> ordered;
        if (terms is null)
        {
            // One chain is read newest first, so a capped read drops its oldest versions, never its latest correction.
            ordered = query.ChainId is not null ? rows.OrderByDescending(f => f.RecordedAt).ThenBy(f => f.Id)
                : query.ChangedFrom is not null || query.ChangedTo is not null || history
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
        return Distinctive(words, counts);
    }

    /// <summary><see cref="DistinctiveWordsAsync"/>'s choice, from each word's count of matching facts in one container.</summary>
    internal static IReadOnlyList<string> Distinctive(IReadOnlyList<string> words, IReadOnlyList<WordCount> counts)
    {
        if (words.Count < 2 || counts.Count == 0 || counts[0].Facts < MinFactsToPrune)
            return words;
        HashSet<string> common = [.. counts.Where(c => c.Matches * 2 > c.Facts).Select(c => c.Word)];
        // A kept word must match something, or a question of stop words and common words would match nothing at all.
        List<string> kept = [.. words.Where(w => !common.Contains(w))];
        return counts.Any(c => c.Matches > 0 && !common.Contains(c.Word)) ? kept : words;
    }

    /// <summary>Below this many facts a word's share says little about how common it is.</summary>
    private const int MinFactsToPrune = 8;

    internal sealed class WordCount
    {
        public string Word { get; init; } = "";
        public int Matches { get; init; }
        public int Facts { get; init; }
    }

    /// <summary>
    /// A recall with words across several containers. Each container leaves out its own common words, so its facts
    /// rank as a recall of that container alone would rank them: counted over all the containers, a project's name is
    /// rare and would outrank the word that picks the fact. A word most of a container's facts share says what the
    /// container is about instead, so containers sharing more of the question's words come first, then the ranks merge.
    /// One statement ranks every container, each with its own words, so the page is read from one snapshot.
    /// </summary>
    private static async Task<IReadOnlyList<MemoryFact>> RankedAcrossAsync(KnowledgeDbContext context,
        IQueryable<MemoryFactEntity> rows, Guid[] containers, IReadOnlyList<string> words, int skip, int take, CancellationToken ct)
    {
        string[] all = [.. words];
        // One pass over the containers' facts: for each word, how many facts it matches in each container, and how many facts there are.
        List<ContainerWordCount> counts = await context.Database.SqlQuery<ContainerWordCount>($"""
            SELECT f.container_id AS "Container", w.word AS "Word",
                count(*) FILTER (WHERE f.search_vector @@ w.query)::int AS "Matches", count(*)::int AS "Facts"
            FROM memory_facts f
            CROSS JOIN (SELECT DISTINCT word, to_tsquery('english', word) AS query FROM unnest({all}) AS word) w
            WHERE f.container_id = ANY({containers})
            GROUP BY f.container_id, w.word
            """).ToListAsync(ct);

        List<(Guid Container, string Terms, int About)> plans = [];
        foreach (IGrouping<Guid, ContainerWordCount> container in counts.GroupBy(c => c.Container))
        {
            List<WordCount> perWord = [.. words.Select(w => new WordCount
            {
                Word = w,
                Matches = container.Where(c => c.Word == w).Sum(c => c.Matches),
                Facts = container.First().Facts,
            })];
            plans.Add((container.Key, TsQuery(Distinctive(words, perWord))!, perWord.Count(c => c.Matches * 2 > c.Facts)));
        }
        if (plans.Count == 0)
            return [];

        // Each fact takes its container's words and about-count: a CASE over the groups of containers that share them.
        List<IGrouping<(string Terms, int About), (Guid Container, string Terms, int About)>> groups =
            [.. plans.GroupBy(p => (p.Terms, p.About))];
        ParameterExpression f = Expression.Parameter(typeof(MemoryFactEntity), "f");
        MemberExpression factContainer = Expression.Property(f, nameof(MemoryFactEntity.ContainerId));
        Expression terms = Parameter(groups[^1].Key.Terms);
        Expression about = Parameter(groups[^1].Key.About);
        for (int i = groups.Count - 2; i >= 0; i--)
        {
            Expression inGroup = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid)],
                Parameter(groups[i].Select(p => p.Container).ToArray()), factContainer);
            terms = Expression.Condition(inGroup, Parameter(groups[i].Key.Terms), terms);
            about = Expression.Condition(inGroup, Parameter(groups[i].Key.About), about);
        }
        Expression<Func<MemoryFactEntity, RankedRow>> plan = Expression.Lambda<Func<MemoryFactEntity, RankedRow>>(
            Expression.MemberInit(Expression.New(typeof(RankedRow)),
                Expression.Bind(typeof(RankedRow).GetProperty(nameof(RankedRow.Fact))!, f),
                Expression.Bind(typeof(RankedRow).GetProperty(nameof(RankedRow.Terms))!, terms),
                Expression.Bind(typeof(RankedRow).GetProperty(nameof(RankedRow.About))!, about)),
            f);

        // A container's about-count is the same for all its facts, so its facts keep the order its own recall gives them.
        List<MemoryFactEntity> page = await rows.Select(plan)
            .Where(r => r.Fact.SearchVector.Matches(EF.Functions.ToTsQuery("english", r.Terms)))
            .OrderByDescending(r => r.About)
            .ThenByDescending(r => r.Fact.SearchVector.Rank(EF.Functions.ToTsQuery("english", r.Terms)))
            .ThenByDescending(r => r.Fact.ValidFrom)
            .ThenByDescending(r => r.Fact.RecordedAt)
            .ThenBy(r => r.Fact.Id)
            .Skip(skip)
            .Take(take)
            .Select(r => r.Fact)
            .Include(r => r.Sources)
            .ToListAsync(ct);
        return [.. page.Select(ToFact)];
    }

    /// <summary>A value read through a property, so EF sends it as a parameter rather than writing it into the SQL.</summary>
    private static MemberExpression Parameter<T>(T value) =>
        Expression.Property(Expression.Constant(new Box<T>(value)), nameof(Box<T>.Value));

    private sealed class Box<T>(T value)
    {
        public T Value { get; } = value;
    }

    private sealed class RankedRow
    {
        public MemoryFactEntity Fact { get; init; } = null!;
        public string Terms { get; init; } = "";
        public int About { get; init; }
    }

    private sealed class ContainerWordCount
    {
        public Guid Container { get; init; }
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
            Scope = (short)fact.ScopeOrDefault,
            Sources = [.. (fact.Sources ?? []).Select((s, i) => new MemoryFactSourceEntity { FactId = id, Ordinal = i, Kind = (short)s.Kind, Ref = s.Ref })],
        };
    }

    private static MemoryFact ToFact(MemoryFactEntity row) => new(
        row.Id, row.ChainId, row.ContainerId, row.Subject, row.Attribute, row.Value,
        row.ValidFrom, row.ValidTo, row.RecordedAt, row.SupersededAt, row.ReplacesId, row.Reason,
        new MemoryCaller(row.AuthorUserId, row.AuthorAgentId, row.AuthorName),
        [.. row.Sources.OrderBy(s => s.Ordinal).Select(s => new MemorySource((MemorySourceKind)s.Kind, s.Ref))],
        (MemoryScope)row.Scope);
}

using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Memory;

// Paged reads. Every one filters visibility in SQL from a readable set worked out once per request, and pages by
// keyset (rows after the last one shown, in a total order ending in the id), so a page deep in a space costs what the
// first one does and no fact is shown twice or skipped while paging.
public sealed partial class PostgresMemoryStore
{
    /// <summary>How many keys one counting batch reads. Tests lower it to make counting stop early.</summary>
    internal int CountBatch { get; init; } = 10_000;

    public async Task<Guid?> SpaceOfAsync(Guid factId, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context.MemoryFacts.AsNoTracking().Where(f => f.Id == factId).Select(f => (Guid?)f.ContainerId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<MemorySource>> CitedSourcesAsync(Guid containerId, Guid? chainId = null, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        IQueryable<MemoryFactEntity> facts = context.MemoryFacts.AsNoTracking().Where(f => f.ContainerId == containerId);
        if (chainId is { } chain)
            facts = facts.Where(f => f.ChainId == chain);
        var cited = await facts.SelectMany(f => f.Sources).Select(s => new { s.Kind, s.Ref }).Distinct().ToListAsync(ct);
        return [.. cited.Select(s => new MemorySource((MemorySourceKind)s.Kind, s.Ref))];
    }

    /// <summary>
    /// The facts <paramref name="viewer"/> can see: no cited source outside the readable set, and team or its own (the
    /// rule of <see cref="MemoryCaller.IsAuthorOf"/>: the same user, or for an author with no user, the same agent also
    /// acting for no user).
    /// </summary>
    internal static IQueryable<MemoryFactEntity> VisibleTo(IQueryable<MemoryFactEntity> rows, MemoryViewer viewer)
    {
        string[] documents = [.. viewer.Readable.Where(s => s.Kind == MemorySourceKind.Document).Select(s => s.Ref).Distinct(StringComparer.Ordinal)];
        string[] nodes = [.. viewer.Readable.Where(s => s.Kind == MemorySourceKind.Node).Select(s => s.Ref).Distinct(StringComparer.Ordinal)];
        const short document = (short)MemorySourceKind.Document;
        const short node = (short)MemorySourceKind.Node;
        const short team = (short)MemoryScope.Team;

        rows = rows.Where(f => f.Sources.All(s =>
            (s.Kind == document && documents.Contains(s.Ref)) || (s.Kind == node && nodes.Contains(s.Ref))));
        if (viewer.Caller.UserId is { } user)
            return rows.Where(f => f.Scope == team || f.AuthorUserId == user);
        if (viewer.Caller.AgentId is { } agent)
            return rows.Where(f => f.Scope == team || (f.AuthorUserId == null && f.AuthorAgentId == agent));
        return rows.Where(f => f.Scope == team);
    }

    private IQueryable<MemoryFactEntity> Filtered(KnowledgeDbContext context, Guid containerId, MemoryFactFilter filter, MemoryViewer viewer)
    {
        IQueryable<MemoryFactEntity> rows = VisibleTo(context.MemoryFacts.AsNoTracking().Where(f => f.ContainerId == containerId), viewer);
        if (!string.IsNullOrWhiteSpace(filter.Subject))
        {
            string subject = ExactILike(filter.Subject.Trim());
            rows = rows.Where(f => EF.Functions.ILike(f.Subject, subject, "\\"));
        }
        if (filter.Author is { } author)
            rows = rows.Where(f => f.AuthorUserId == author || f.AuthorAgentId == author);
        if (filter.Cites == MemoryCites.Some)
            rows = rows.Where(f => f.Sources.Any());
        else if (filter.Cites == MemoryCites.None)
            rows = rows.Where(f => !f.Sources.Any());
        if (filter.From is { } from)
            rows = rows.Where(f => f.RecordedAt >= from);
        if (filter.To is { } to)
            rows = rows.Where(f => f.RecordedAt <= to);
        rows = Checked(rows, filter.Check, clock.GetUtcNow());
        return filter.Status == MemoryFactStatus.Current ? Current(rows) : rows;
    }

    private IQueryable<MemoryFactEntity> Current(IQueryable<MemoryFactEntity> rows)
    {
        DateTimeOffset now = clock.GetUtcNow();
        return rows.Where(f => f.SupersededAt == null && (f.ValidFrom == null || f.ValidFrom <= now) && (f.ValidTo == null || f.ValidTo > now));
    }

    /// <summary>
    /// The search terms a list ranks by: the cursor's when paging on, else the text's distinctive words. A cursor's terms
    /// are rebuilt from their words, since a client can send any cursor and to_tsquery fails on malformed syntax.
    /// </summary>
    private static async Task<string?> TermsAsync(KnowledgeDbContext context, Guid containerId, MemoryFactFilter filter, MemoryFactKey? after, CancellationToken ct) =>
        after?.Terms is { } carried ? TsQuery(carried) : TsQuery(await DistinctiveWordsAsync(context, containerId, QueryWords(filter.Text), ct));

    public async Task<IReadOnlyList<MemoryListedFact>> PageAsync(
        Guid containerId, MemoryFactFilter filter, MemoryFactSort sort, MemoryFactKey? after, int take, MemoryViewer viewer,
        CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        IQueryable<MemoryFactEntity> rows = Filtered(context, containerId, filter, viewer);
        string? terms = await TermsAsync(context, containerId, filter, after, ct);
        if (terms is not null)
            rows = rows.Where(f => f.SearchVector.Matches(EF.Functions.ToTsQuery("english", terms)));
        else if (sort == MemoryFactSort.Relevance)
            sort = MemoryFactSort.Recorded;

        switch (sort)
        {
            case MemoryFactSort.Subject:
                if (after is not null)
                {
                    string subject = after.Subject ?? "", attribute = after.Attribute ?? "";
                    Guid id = after.Id;
                    rows = rows.Where(f => EF.Functions.GreaterThan(
                        ValueTuple.Create(f.Subject, f.Attribute, f.Id), ValueTuple.Create(subject, attribute, id)));
                }
                return Listed(await rows.OrderBy(f => f.Subject).ThenBy(f => f.Attribute).ThenBy(f => f.Id)
                    .Take(take).Include(f => f.Sources).ToListAsync(ct),
                    f => new MemoryFactKey(f.Id, Subject: f.Subject, Attribute: f.Attribute));

            case MemoryFactSort.ValidFrom:
                if (after is not null)
                {
                    DateTimeOffset from = after.ValidFrom ?? DateTimeOffset.MinValue;
                    Guid id = after.Id;
                    rows = rows.Where(f => EF.Functions.LessThan(
                        ValueTuple.Create(f.ValidFrom ?? DateTimeOffset.MinValue, f.Id), ValueTuple.Create(from, id)));
                }
                return Listed(await rows.OrderByDescending(f => f.ValidFrom ?? DateTimeOffset.MinValue).ThenByDescending(f => f.Id)
                    .Take(take).Include(f => f.Sources).ToListAsync(ct),
                    f => new MemoryFactKey(f.Id, ValidFrom: f.ValidFrom));

            case MemoryFactSort.Relevance:
                return await RankedAsync(rows, terms!, after, take, ct);

            default:
                if (after is not null)
                {
                    DateTimeOffset recorded = after.Recorded ?? DateTimeOffset.MaxValue;
                    Guid id = after.Id;
                    rows = rows.Where(f => EF.Functions.LessThan(
                        ValueTuple.Create(f.RecordedAt, f.Id), ValueTuple.Create(recorded, id)));
                }
                return Listed(await rows.OrderByDescending(f => f.RecordedAt).ThenByDescending(f => f.Id)
                    .Take(take).Include(f => f.Sources).ToListAsync(ct),
                    f => new MemoryFactKey(f.Id, Recorded: f.RecordedAt));
        }
    }

    private List<MemoryListedFact> Listed(List<MemoryFactEntity> page, Func<MemoryFactEntity, MemoryFactKey> key) =>
        [.. page.Select(f => new MemoryListedFact(ToFact(f), key(f)))];

    /// <summary>Best match first, by (rank, id) descending, stopping at the best <see cref="IMemoryStore.MaxSearchResults"/>.</summary>
    private async Task<IReadOnlyList<MemoryListedFact>> RankedAsync(
        IQueryable<MemoryFactEntity> rows, string terms, MemoryFactKey? after, int take, CancellationToken ct)
    {
        int position = Math.Max(0, after?.Position ?? 0);
        take = Math.Min(take, IMemoryStore.MaxSearchResults - position);
        if (take <= 0)
            return [];
        if (after is not null)
        {
            float rank = after.Rank ?? float.MaxValue;
            Guid id = after.Id;
            rows = rows.Where(f => EF.Functions.LessThan(
                ValueTuple.Create(f.SearchVector.Rank(EF.Functions.ToTsQuery("english", terms)), f.Id), ValueTuple.Create(rank, id)));
        }
        var page = await rows
            .Select(f => new { Row = f, Sources = f.Sources.ToList(), Rank = f.SearchVector.Rank(EF.Functions.ToTsQuery("english", terms)) })
            .OrderByDescending(r => r.Rank).ThenByDescending(r => r.Row.Id)
            .Take(take)
            .ToListAsync(ct);
        return [.. page.Select((r, i) =>
        {
            r.Row.Sources = r.Sources;
            return new MemoryListedFact(ToFact(r.Row), new MemoryFactKey(r.Row.Id, Rank: r.Rank, Terms: terms, Position: position + i + 1));
        })];
    }

    public async Task<(long Count, bool Capped)> CountAsync(
        Guid containerId, MemoryFactFilter filter, MemoryViewer viewer, TimeSpan budget, CancellationToken ct = default)
    {
        long started = clock.GetTimestamp();
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        IQueryable<MemoryFactEntity> rows = Filtered(context, containerId, filter, viewer);
        string? terms = await TermsAsync(context, containerId, filter, null, ct);
        long most = long.MaxValue;
        if (terms is not null)
        {
            rows = rows.Where(f => f.SearchVector.Matches(EF.Functions.ToTsQuery("english", terms)));
            most = IMemoryStore.MaxSearchResults;
        }

        // In batches of keys along the current-facts index, so a count that runs long stops with a lower bound rather
        // than with nothing. The timer stops a batch that is itself slow.
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(budget);
        long count = 0;
        DateTimeOffset? lastRecorded = null;
        Guid lastId = Guid.Empty;
        try
        {
            while (true)
            {
                IQueryable<MemoryFactEntity> batch = rows;
                if (lastRecorded is { } recorded)
                {
                    Guid id = lastId;
                    batch = batch.Where(f => EF.Functions.LessThan(ValueTuple.Create(f.RecordedAt, f.Id), ValueTuple.Create(recorded, id)));
                }
                var keys = await batch.OrderByDescending(f => f.RecordedAt).ThenByDescending(f => f.Id)
                    .Select(f => new { f.RecordedAt, f.Id }).Take(CountBatch).ToListAsync(timer.Token);
                count += keys.Count;
                if (count > most)
                    return (most, true);
                if (keys.Count < CountBatch)
                    return (count, false);
                if (clock.GetElapsedTime(started) >= budget)
                    return (count, true);
                (lastRecorded, lastId) = (keys[^1].RecordedAt, keys[^1].Id);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (count, true);
        }
    }

    public async Task<IReadOnlyList<(string Key, MemorySubject Subject)>> SubjectsAsync(
        Guid containerId, string? text, MemoryFactStatus status, string? after, int take, MemoryViewer viewer,
        CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        IQueryable<MemoryFactEntity> rows = Filtered(context, containerId, new MemoryFactFilter(Status: status), viewer);
        if (!string.IsNullOrWhiteSpace(text))
        {
            string pattern = "%" + ExactILike(text.Trim()) + "%";
            rows = rows.Where(f => EF.Functions.ILike(f.Subject, pattern, "\\"));
        }
        // Translated to SQL lower() and >, which follow the database's collation on both sides of the keyset, so the
        // culture and ordinal-comparison analyzers do not apply.
#pragma warning disable CA1304, CA1309, CA1311
        var groups = rows.GroupBy(f => f.Subject.ToLower())
            .Select(g => new { Key = g.Key, Subject = g.Min(f => f.Subject), Facts = g.LongCount() });
        if (after is not null)
            groups = groups.Where(g => string.Compare(g.Key, after) > 0);
#pragma warning restore CA1304, CA1309, CA1311
        var page = await groups.OrderBy(g => g.Key).Take(take).ToListAsync(ct);
        return [.. page.Select(g => (g.Key, new MemorySubject(g.Subject!, g.Facts)))];
    }

    public async Task<IReadOnlyList<MemoryFact>> HistoryAsync(
        Guid containerId, Guid chainId, MemoryVersionKey? after, int take, MemoryViewer viewer, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        IQueryable<MemoryFactEntity> rows = VisibleTo(
            context.MemoryFacts.AsNoTracking().Where(f => f.ContainerId == containerId && f.ChainId == chainId), viewer);
        if (after is not null)
        {
            DateTimeOffset recorded = after.Recorded;
            Guid id = after.Id;
            rows = rows.Where(f => EF.Functions.LessThan(ValueTuple.Create(f.RecordedAt, f.Id), ValueTuple.Create(recorded, id)));
        }
        List<MemoryFactEntity> page = await rows.OrderByDescending(f => f.RecordedAt).ThenByDescending(f => f.Id)
            .Take(take).Include(f => f.Sources).ToListAsync(ct);
        return [.. page.Select(ToFact)];
    }

    public async Task<long> HistoryCountAsync(Guid containerId, Guid chainId, MemoryViewer viewer, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        return await VisibleTo(context.MemoryFacts.AsNoTracking().Where(f => f.ContainerId == containerId && f.ChainId == chainId), viewer)
            .LongCountAsync(ct);
    }
}

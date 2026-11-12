using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// Memory in a list, with the visibility the real service gives a reader who can see everything. Facts land in
/// <paramref name="space"/> unless another space is named, and each read is limited to the space it asks about.
/// </summary>
internal sealed class FakeMemory(Guid space) : IMemoryService
{
    /// <summary>A document source the fake shows by the title "Ops runbook".</summary>
    public static readonly MemorySource Runbook = new(MemorySourceKind.Document, Guid.NewGuid().ToString());

    private readonly List<MemoryFact> facts = [];

    /// <summary>The filter of each page listed.</summary>
    public List<MemoryFactFilter> Lists { get; } = [];

    /// <summary>The sort of each page listed.</summary>
    public List<MemoryFactSort> Sorts { get; } = [];

    /// <summary>The filter of each count.</summary>
    public List<MemoryFactFilter> Counts { get; } = [];

    public List<(Guid FactId, MemoryCorrection Kind, NewMemoryFact Replacement)> Corrections { get; } = [];

    /// <summary>Each new fact written, with the space it was written to.</summary>
    public List<(Guid Space, NewMemoryFact Fact)> Remembered { get; } = [];

    public List<Guid> Forgotten { get; } = [];

    public bool DenyReads { get; set; }

    public string? RefuseCorrections { get; set; }

    /// <summary>When set, every new fact is refused with this reason.</summary>
    public string? RefuseRemembers { get; set; }

    private int countsAnswered;

    /// <summary>How many counts have answered.</summary>
    public int CountsAnswered => Volatile.Read(ref countsAnswered);

    private bool holdingCounts;
    private readonly List<TaskCompletionSource> countGates = [];

    /// <summary>Counts wait until released, each answering with the total when it started.</summary>
    public void HoldCounts() => holdingCounts = true;

    public void ReleaseCounts()
    {
        holdingCounts = false;
        foreach (var gate in countGates)
            gate.TrySetResult();
    }

    /// <summary>Releases the count started <paramref name="index"/>th, from 0.</summary>
    public void ReleaseCount(int index) => countGates[index].SetResult();

    public MemoryFact Add(
        string subject, string attribute, string value, DateTimeOffset? from = null, IReadOnlyList<MemorySource>? sources = null,
        DateTimeOffset? recorded = null, MemoryCaller? author = null, Guid? inSpace = null, MemoryScope scope = MemoryScope.Personal)
    {
        Guid id = Guid.NewGuid();
        var fact = new MemoryFact(id, id, inSpace ?? space, subject, attribute, value, from, null, recorded ?? DateTimeOffset.UtcNow.AddDays(-2),
            null, null, null, author ?? new MemoryCaller(Guid.NewGuid(), Guid.NewGuid(), "pat"), sources ?? [], scope);
        lock (facts)
            facts.Add(fact);
        return fact;
    }

    public MemoryFact Correct(MemoryFact old, MemoryCorrection kind, string value, DateTimeOffset? from = null, string? reason = null)
    {
        facts.Remove(old);
        facts.Add(kind == MemoryCorrection.Changed ? old with { ValidTo = from } : old with { SupersededAt = DateTimeOffset.UtcNow });
        var fact = old with
        {
            Id = Guid.NewGuid(),
            Value = value,
            ValidFrom = kind == MemoryCorrection.Changed ? from : old.ValidFrom,
            ValidTo = null,
            SupersededAt = null,
            ReplacesId = old.Id,
            Reason = reason,
            RecordedAt = old.RecordedAt.AddMinutes(1),
        };
        facts.Add(fact);
        return fact;
    }

    public Task<MemoryWriteResult> RememberAsync(Guid containerId, NewMemoryFact fact, MemoryCaller caller, CancellationToken ct = default)
    {
        Remembered.Add((containerId, fact));
        if (RefuseRemembers is { } refusal)
            return Task.FromResult(MemoryWriteResult.Failed(refusal, MemoryWriteFailure.Forbidden));
        MemoryFact written = Add(fact.Subject, fact.Attribute, fact.Value, fact.ValidFrom, fact.Sources, author: caller,
            inSpace: containerId, scope: fact.ScopeOrDefault);
        return Task.FromResult(new MemoryWriteResult(written, null, null));
    }

    public Task<MemoryWriteResult> CorrectAsync(
        Guid factId, MemoryCorrection kind, NewMemoryFact replacement, MemoryCaller caller, bool teamWriter,
        CancellationToken ct = default)
    {
        Corrections.Add((factId, kind, replacement));
        if (RefuseCorrections is { } refusal)
            return Task.FromResult(MemoryWriteResult.Failed(refusal));
        MemoryFact old = facts.Single(f => f.Id == factId);
        MemoryFact written = Correct(old, kind, replacement.Value, replacement.ValidFrom, replacement.Reason);
        return Task.FromResult(new MemoryWriteResult(written, facts.Single(f => f.Id == factId), null));
    }

    public Task<(int Deleted, MemoryWriteFailure Failure)> ForgetAsync(
        Guid factId, MemoryCaller caller, bool teamWriter, CancellationToken ct = default)
    {
        Forgotten.Add(factId);
        Guid chain = facts.Single(f => f.Id == factId).ChainId;
        int deleted = facts.RemoveAll(f => f.ChainId == chain);
        return Task.FromResult((deleted, MemoryWriteFailure.None));
    }

    /// <summary>Facts the reader can no longer see, as when access to their sources is taken away.</summary>
    public HashSet<Guid> Hidden { get; } = [];

    /// <summary>History reads for this chain wait until <see cref="ReleaseHeldChain"/>.</summary>
    public Guid? HeldChain { get; set; }

    private readonly TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseHeldChain() => held.SetResult();

    private static RecalledFact Shown(MemoryFact f) => new(f,
        [.. f.Sources.Select(s => new ShownMemorySource(s.Kind, s.Ref, s == Runbook ? "Ops runbook" : null))]);

    private IEnumerable<MemoryFact> Matching(Guid containerId, MemoryFactFilter filter) => facts.Where(f =>
        f.ContainerId == containerId
        && !Hidden.Contains(f.Id)
        && (filter.Status == MemoryFactStatus.All || (f.SupersededAt is null && f.ValidTo is null))
        && (filter.Text is null || f.Value.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)
            || f.Subject.Contains(filter.Text, StringComparison.OrdinalIgnoreCase))
        && (filter.Subject is null || string.Equals(f.Subject, filter.Subject, StringComparison.OrdinalIgnoreCase))
        && (filter.Author is null || f.Author.UserId == filter.Author || f.Author.AgentId == filter.Author)
        && (filter.Cites == MemoryCites.Any || (filter.Cites == MemoryCites.Some) == (f.Sources.Count > 0))
        && (filter.From is null || f.RecordedAt >= filter.From)
        && (filter.To is null || f.RecordedAt <= filter.To));

    public Task<MemoryRecall> RecallAsync(MemoryQuery query, MemoryCaller caller, CancellationToken ct = default) =>
        throw new NotSupportedException("The memory list pages through list, not recall.");

    public Task<MemoryFactPage> ListAsync(
        Guid containerId, MemoryFactFilter filter, MemoryFactSort sort, MemoryFactKey? after, int limit, MemoryCaller caller,
        CancellationToken ct = default)
    {
        lock (facts)
        {
            Lists.Add(filter);
            Sorts.Add(sort);
            if (DenyReads)
                return Task.FromResult(MemoryFactPage.DeniedResult);
            // Newest recorded first, whatever the sort: the fake's order only has to be stable for its cursor.
            IEnumerable<MemoryFact> found = Matching(containerId, filter).OrderByDescending(f => f.RecordedAt).ThenByDescending(f => f.Id);
            if (after is { Recorded: { } recorded })
                found = found.SkipWhile(f => f.RecordedAt > recorded || (f.RecordedAt == recorded && f.Id.CompareTo(after.Id) >= 0));
            List<MemoryFact> rest = [.. found.Take(limit + 1)];
            List<MemoryFact> page = [.. rest.Take(limit)];
            return Task.FromResult(new MemoryFactPage([.. page.Select(Shown)],
                rest.Count > limit ? new MemoryFactKey(page[^1].Id, page[^1].RecordedAt) : null));
        }
    }

    public async Task<MemoryFactCount> CountAsync(Guid containerId, MemoryFactFilter filter, MemoryCaller caller, CancellationToken ct = default)
    {
        int total;
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (facts)
        {
            Counts.Add(filter);
            total = Matching(containerId, filter).Count();
            countGates.Add(gate);
        }
        if (!holdingCounts)
            gate.SetResult();
        await gate.Task;
        Interlocked.Increment(ref countsAnswered);
        return new MemoryFactCount(total, false);
    }

    public Task<MemorySubjectPage> SubjectsAsync(
        Guid containerId, string? text, MemoryFactStatus status, string? after, int limit, MemoryCaller caller,
        CancellationToken ct = default)
    {
        lock (facts)
        {
            List<MemorySubject> all = [.. Matching(containerId, new MemoryFactFilter(Status: status))
                .Where(f => text is null || f.Subject.Contains(text, StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => f.Subject.ToLowerInvariant())
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .SkipWhile(g => after is not null && string.CompareOrdinal(g.Key, after) <= 0)
                .Select(g => new MemorySubject(g.First().Subject, g.Count()))];
            List<MemorySubject> page = [.. all.Take(limit)];
            return Task.FromResult(new MemorySubjectPage(page, all.Count > limit ? page[^1].Subject.ToLowerInvariant() : null));
        }
    }

    public Task<Guid?> SpaceOfAsync(Guid factId, CancellationToken ct = default)
    {
        lock (facts)
            return Task.FromResult(facts.FirstOrDefault(f => f.Id == factId) is { } fact ? fact.ContainerId : (Guid?)null);
    }

    /// <summary>The fact each history read asked about.</summary>
    public List<Guid> HistoryReads { get; } = [];

    public async Task<MemoryHistoryPage> HistoryAsync(
        Guid containerId, Guid factId, MemoryVersionKey? after, int limit, MemoryCaller caller, CancellationToken ct = default)
    {
        HistoryReads.Add(factId);
        if (facts.FirstOrDefault(f => f.Id == factId) is not { } of)
            return MemoryHistoryPage.Missing;
        if (of.ChainId == HeldChain)
            await held.Task;
        if (DenyReads)
            return MemoryHistoryPage.DeniedResult;
        if (Hidden.Contains(factId))
            return MemoryHistoryPage.Missing;
        // Newest first, as the store reads a chain.
        List<MemoryFact> chain = [.. facts.Where(f => f.ChainId == of.ChainId && !Hidden.Contains(f.Id))
            .OrderByDescending(f => f.RecordedAt).ThenByDescending(f => f.Id)];
        List<MemoryFact> rest = after is null
            ? chain
            : [.. chain.SkipWhile(f => f.RecordedAt > after.Recorded || (f.RecordedAt == after.Recorded && f.Id.CompareTo(after.Id) >= 0))];
        List<MemoryFact> page = [.. rest.Take(limit)];
        MemoryVersionKey? next = rest.Count > limit ? new MemoryVersionKey(page[^1].RecordedAt, page[^1].Id) : null;
        return new MemoryHistoryPage([.. page.Select(Shown)], chain.Count, next);
    }

    public Task<MemoryWriteResult> ConfirmAsync(
        Guid factId, MemoryCheckBy? check, MemoryCaller caller, bool teamWriter, CancellationToken ct = default) =>
        throw new NotSupportedException("The UI does not confirm facts.");

    public Task<IReadOnlyDictionary<string, int>> CheckDefaultsAsync(Guid containerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    public Task<string?> SetCheckDefaultsAsync(
        Guid containerId, IReadOnlyDictionary<string, int> defaults, MemoryCaller caller, CancellationToken ct = default) =>
        throw new NotSupportedException("The UI does not set check-by defaults.");
}

namespace Connapse.Core.Interfaces;

/// <summary>
/// Agent memory's tables, with no permission checks: every caller goes through <see cref="IMemoryService"/>.
/// Facts are never changed in place except to end or supersede them, and only <see cref="ForgetAsync"/> deletes.
/// </summary>
public interface IMemoryStore
{
    Task<MemoryFact> AddAsync(Guid containerId, NewMemoryFact fact, MemoryCaller author, CancellationToken ct = default);

    /// <summary>
    /// Ends or supersedes <paramref name="factId"/> and writes its replacement, in one transaction. Returns an error
    /// when the fact is not in the container, is already superseded or ended, or the dates do not follow it.
    /// </summary>
    Task<MemoryWriteResult> CorrectAsync(
        Guid containerId, Guid factId, MemoryCorrection kind, NewMemoryFact replacement, MemoryCaller author, CancellationToken ct = default);

    /// <summary>
    /// Deletes every fact of <paramref name="factId"/>'s correction chain and their sources, in one transaction with the
    /// chain locked, but only when <paramref name="allow"/> approves the whole chain. Returns how many facts went, or
    /// null when the fact is not there or the chain was refused.
    /// </summary>
    Task<int?> ForgetAsync(Guid containerId, Guid factId, Func<IReadOnlyList<MemoryFact>, Task<bool>> allow, CancellationToken ct = default);

    Task<MemoryFact?> GetAsync(Guid containerId, Guid factId, CancellationToken ct = default);

    /// <summary>One page of matching facts, best match first (newest first without text), skipping <paramref name="skip"/>.</summary>
    Task<IReadOnlyList<MemoryFact>> QueryAsync(MemoryQuery query, int skip, int take, CancellationToken ct = default);

    /// <summary>The names of the documents among <paramref name="documentIds"/>, for showing sources.</summary>
    Task<IReadOnlyDictionary<string, string>> DocumentTitlesAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default);
}

/// <summary>
/// Agent memory with its rules: the writer must be able to read every source it cites, every write is audited, and
/// a reader sees a fact only when it can read every one of its sources (a sourceless fact only its author).
/// Access is checked on every call; when it cannot be checked, reads return <see cref="MemoryRecall.DeniedResult"/>
/// and writes fail.
/// </summary>
public interface IMemoryService
{
    Task<MemoryWriteResult> RememberAsync(Guid containerId, NewMemoryFact fact, MemoryCaller caller, CancellationToken ct = default);

    Task<MemoryWriteResult> CorrectAsync(
        Guid containerId, Guid factId, MemoryCorrection kind, NewMemoryFact replacement, MemoryCaller caller, CancellationToken ct = default);

    /// <summary>Deletes the fact's whole correction chain. Returns the number deleted, or an error when the caller may not see it.</summary>
    Task<(int Deleted, string? Error)> ForgetAsync(Guid containerId, Guid factId, MemoryCaller caller, CancellationToken ct = default);

    Task<MemoryRecall> RecallAsync(MemoryQuery query, MemoryCaller caller, CancellationToken ct = default);
}

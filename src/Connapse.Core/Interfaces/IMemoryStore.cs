namespace Connapse.Core.Interfaces;

/// <summary>
/// Agent memory's tables, with no permission checks: every caller goes through <see cref="IMemoryService"/>.
/// Facts are never changed in place except to end or supersede them, and only <see cref="ForgetAsync"/> deletes.
/// </summary>
public interface IMemoryStore
{
    Task<MemoryFact> AddAsync(Guid containerId, NewMemoryFact fact, MemoryCaller author, CancellationToken ct = default);

    /// <summary>
    /// Ends or supersedes <paramref name="factId"/> and writes its replacement in the same container, in one
    /// transaction. Facts are found by id wherever they are, since moving memory into spaces changes a fact's container
    /// but never its id. Returns an error when there is no such fact, it is already superseded or ended, or the dates do not follow it, or the
    /// error <paramref name="check"/> returns for the chain's versions, which it is given with the chain locked. The
    /// replacement takes the scope the old fact has under that lock, whatever <paramref name="replacement"/> says.
    /// </summary>
    Task<MemoryWriteResult> CorrectAsync(
        Guid factId, MemoryCorrection kind, NewMemoryFact replacement, MemoryCaller author,
        Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>> check, CancellationToken ct = default);

    /// <summary>
    /// Deletes every fact of <paramref name="factId"/>'s correction chain and their sources, in one transaction with the
    /// chain locked, but only when <paramref name="allow"/> approves the whole chain. Returns how many facts went, or
    /// null when the fact is not there or the chain was refused.
    /// </summary>
    Task<int?> ForgetAsync(Guid factId, Func<IReadOnlyList<MemoryFact>, Task<bool>> allow, CancellationToken ct = default);

    Task<MemoryFact?> GetAsync(Guid factId, CancellationToken ct = default);

    /// <summary>One page of matching facts, best match first (newest first without text), skipping <paramref name="skip"/>.</summary>
    Task<IReadOnlyList<MemoryFact>> QueryAsync(MemoryQuery query, int skip, int take, CancellationToken ct = default);

    /// <summary>The names of the documents among <paramref name="documentIds"/>, for showing sources.</summary>
    Task<IReadOnlyDictionary<string, string>> DocumentTitlesAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default);

    /// <summary>
    /// Every distinct source the container's facts cite (only <paramref name="chainId"/>'s versions when given), so the
    /// readable set can be worked out once before a read.
    /// </summary>
    Task<IReadOnlyList<MemorySource>> CitedSourcesAsync(Guid containerId, Guid? chainId = null, CancellationToken ct = default);

    /// <summary>Every distinct source the facts of all of <paramref name="containerIds"/> cite, in one read.</summary>
    Task<IReadOnlyList<MemorySource>> CitedSourcesAcrossAsync(IReadOnlyCollection<Guid> containerIds, CancellationToken ct = default);

    /// <summary>The first <paramref name="take"/> facts matching <paramref name="query"/> that <paramref name="viewer"/> can see, in <see cref="QueryAsync"/>'s order.</summary>
    Task<IReadOnlyList<MemoryFact>> VisibleAsync(MemoryQuery query, MemoryViewer viewer, int take, CancellationToken ct = default);

    /// <summary>
    /// The next <paramref name="take"/> facts of the container that <paramref name="viewer"/> can see, after
    /// <paramref name="after"/> in <paramref name="sort"/>'s order. A best-match list stops at the best
    /// <see cref="MaxSearchResults"/>.
    /// </summary>
    Task<IReadOnlyList<MemoryListedFact>> PageAsync(
        Guid containerId, MemoryFactFilter filter, MemoryFactSort sort, MemoryFactKey? after, int take, MemoryViewer viewer,
        CancellationToken ct = default);

    /// <summary>
    /// How many facts of the container <paramref name="viewer"/> can see. Counting stops once
    /// <paramref name="budget"/> has passed, and the count so far is returned as capped.
    /// </summary>
    Task<(long Count, bool Capped)> CountAsync(
        Guid containerId, MemoryFactFilter filter, MemoryViewer viewer, TimeSpan budget, CancellationToken ct = default);

    /// <summary>
    /// The next <paramref name="take"/> subjects after <paramref name="after"/> (lower-cased), A to Z, with how many
    /// facts <paramref name="viewer"/> can see under each; subjects with none are left out. <paramref name="text"/>
    /// keeps subjects containing it, in any case. Each comes with its key, the lower-cased subject a next page starts after.
    /// </summary>
    Task<IReadOnlyList<(string Key, MemorySubject Subject)>> SubjectsAsync(
        Guid containerId, string? text, MemoryFactStatus status, string? after, int take, MemoryViewer viewer,
        CancellationToken ct = default);

    /// <summary>The next <paramref name="take"/> versions of a chain that <paramref name="viewer"/> can see, newest first, after <paramref name="after"/>.</summary>
    Task<IReadOnlyList<MemoryFact>> HistoryAsync(
        Guid containerId, Guid chainId, MemoryVersionKey? after, int take, MemoryViewer viewer, CancellationToken ct = default);

    /// <summary>How many versions of a chain <paramref name="viewer"/> can see.</summary>
    Task<long> HistoryCountAsync(Guid containerId, Guid chainId, MemoryViewer viewer, CancellationToken ct = default);

    /// <summary>The container a fact is in, or null when there is no such fact. Says nothing about who may read it.</summary>
    Task<Guid?> SpaceOfAsync(Guid factId, CancellationToken ct = default);

    /// <summary>
    /// Which of <paramref name="text"/>'s words the facts <paramref name="factIds"/> of <paramref name="containerId"/>
    /// hold. Says nothing about who may read them, so the ids must be facts the caller was already shown.
    /// </summary>
    Task<MemoryWordCoverage> CoverageAsync(
        Guid containerId, string text, IReadOnlyCollection<Guid> factIds, CancellationToken ct = default);

    /// <summary>The most matches a search lists.</summary>
    const int MaxSearchResults = 1000;
}

/// <summary>
/// Agent memory with its rules: the writer must be able to read every source it cites, every write is audited, and
/// a reader sees a fact only when it can read every one of its sources and the fact is team or the reader's own.
/// Writing any fact, personal or team, needs write access, which the caller's surface must check before calling; this
/// service checks it only for team facts, passed as <c>teamWriter</c>. Access is checked on every call; when it cannot be
/// checked, reads return <see cref="MemoryRecall.DeniedResult"/> and writes fail.
/// </summary>
public interface IMemoryService
{
    Task<MemoryWriteResult> RememberAsync(Guid containerId, NewMemoryFact fact, MemoryCaller caller, CancellationToken ct = default);

    /// <summary>
    /// Corrects the fact with this id in whichever space or container holds it. A fact the caller cannot see is reported
    /// as missing.
    /// </summary>
    Task<MemoryWriteResult> CorrectAsync(
        Guid factId, MemoryCorrection kind, NewMemoryFact replacement, MemoryCaller caller, bool teamWriter,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes the fact's whole correction chain, in whichever space or container holds it. Returns the number deleted, or why not: missing when the caller may
    /// not see every version, forbidden when a version is team and the caller is not a team writer.
    /// </summary>
    Task<(int Deleted, MemoryWriteFailure Failure)> ForgetAsync(Guid factId, MemoryCaller caller, bool teamWriter, CancellationToken ct = default);

    Task<MemoryRecall> RecallAsync(MemoryQuery query, MemoryCaller caller, CancellationToken ct = default);

    /// <summary>The next page of a container's facts the caller can see, after <paramref name="after"/>; <paramref name="limit"/> is clamped to 1–100.</summary>
    Task<MemoryFactPage> ListAsync(
        Guid containerId, MemoryFactFilter filter, MemoryFactSort sort, MemoryFactKey? after, int limit, MemoryCaller caller,
        CancellationToken ct = default);

    /// <summary>How many of a container's facts the caller can see, stopping after about two seconds.</summary>
    Task<MemoryFactCount> CountAsync(Guid containerId, MemoryFactFilter filter, MemoryCaller caller, CancellationToken ct = default);

    /// <summary>The next page of subjects with visible facts, after <paramref name="after"/>; <paramref name="limit"/> is clamped to 1–100.</summary>
    Task<MemorySubjectPage> SubjectsAsync(
        Guid containerId, string? text, MemoryFactStatus status, string? after, int limit, MemoryCaller caller,
        CancellationToken ct = default);

    /// <summary>
    /// The next page of the versions of <paramref name="factId"/>'s chain the caller can see, newest first, and how
    /// many there are. Not found when the fact is missing or the caller cannot see it.
    /// </summary>
    Task<MemoryHistoryPage> HistoryAsync(
        Guid containerId, Guid factId, MemoryVersionKey? after, int limit, MemoryCaller caller, CancellationToken ct = default);

    /// <summary>
    /// The container a fact is in, or null when there is no such fact. Says nothing about who may read it, so the
    /// caller must check access to that container and answer as for a missing fact when it can't.
    /// </summary>
    Task<Guid?> SpaceOfAsync(Guid factId, CancellationToken ct = default);

    /// <summary>
    /// Which of <paramref name="text"/>'s words the facts <paramref name="factIds"/> of <paramref name="containerId"/>
    /// hold; null when it could not be worked out. Reveals only which words the given facts hold, so the ids must be
    /// facts the caller was just shown.
    /// </summary>
    Task<MemoryWordCoverage?> CoverageAsync(
        Guid containerId, string text, IReadOnlyCollection<Guid> factIds, CancellationToken ct = default);
}

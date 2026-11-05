namespace Connapse.Core.Interfaces;

/// <summary>The Memory page's view of spaces: listing with counts, making personal spaces, delete impact, and stars.</summary>
public interface IMemorySpaceStore
{
    /// <summary>
    /// Every place <paramref name="viewer"/> may see memory in: their personal spaces, every team space, and each
    /// document container holding at least one current fact they may see. <paramref name="text"/>, when given, must
    /// appear in the name or description. Null when access to the facts' sources could not be checked.
    /// <paramref name="withEnded"/> also lists document containers whose only facts they may see are no longer
    /// current, for reads of history or a past date; counts stay counts of current facts.
    /// </summary>
    Task<IReadOnlyList<MemorySpaceSummary>?> ListAsync(MemoryCaller viewer, string? text, bool withEnded = false, CancellationToken ct = default);

    /// <summary>
    /// Every place <paramref name="viewer"/> may see memory in, with bounds on its numbers and no source checked: their
    /// personal spaces, every team space, and each document container holding a current fact in their scope.
    /// <paramref name="text"/>, when given, must appear in the name or description.
    /// </summary>
    Task<IReadOnlyList<MemorySpaceCandidate>> CandidatesAsync(MemoryCaller viewer, string? text, CancellationToken ct = default);

    /// <summary>
    /// The exact numbers of the spaces <paramref name="containerIds"/> names for <paramref name="viewer"/>, checking only
    /// the sources their facts cite. A space the viewer may not see is left out. Null when access could not be checked.
    /// </summary>
    Task<IReadOnlyDictionary<string, MemorySpaceTally>?> TallyAsync(
        MemoryCaller viewer, IReadOnlyCollection<string> containerIds, CancellationToken ct = default);

    /// <summary>
    /// Which of <paramref name="containerIds"/> hold at least one current fact <paramref name="viewer"/> may read. Each
    /// space's likeliest fact is checked first, and the rest of its facts only when that one is hidden. Null when access
    /// could not be checked.
    /// </summary>
    Task<IReadOnlySet<string>?> HoldingReadableAsync(
        MemoryCaller viewer, IReadOnlyCollection<string> containerIds, CancellationToken ct = default);

    /// <summary>
    /// A new personal space owned by <paramref name="owner"/>, or null when the owner already has one with this name.
    /// Throws <see cref="ArgumentException"/> when the name isn't a valid container name or the owner has no id.
    /// </summary>
    Task<Container?> CreatePersonalAsync(MemoryCaller owner, string name, string? description, CancellationToken ct = default);

    /// <summary>
    /// What deleting the space removes that <paramref name="viewer"/> may read: the facts, every version of them, and
    /// their authors. Facts the viewer can't read are deleted too but not counted, so the report reveals nothing about
    /// them. Null when access to the facts' sources could not be checked.
    /// </summary>
    Task<MemorySpaceImpact?> ImpactAsync(Guid containerId, MemoryCaller viewer, CancellationToken ct = default);

    /// <summary>Stars the space for <paramref name="caller"/> alone. Starring twice is the same as once.</summary>
    Task StarAsync(Guid containerId, MemoryCaller caller, CancellationToken ct = default);

    /// <summary>Removes <paramref name="caller"/>'s star, if there is one.</summary>
    Task UnstarAsync(Guid containerId, MemoryCaller caller, CancellationToken ct = default);
}

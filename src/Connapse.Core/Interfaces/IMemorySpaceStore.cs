namespace Connapse.Core.Interfaces;

/// <summary>The Memory page's view of spaces: listing with counts, making personal spaces, delete impact, and stars.</summary>
public interface IMemorySpaceStore
{
    /// <summary>
    /// Every place <paramref name="viewer"/> may see memory in: their personal spaces, every team space, and each
    /// document container holding at least one current fact they may see. <paramref name="text"/>, when given, must
    /// appear in the name or description. Null when access to the facts' sources could not be checked.
    /// </summary>
    Task<IReadOnlyList<MemorySpaceSummary>?> ListAsync(MemoryCaller viewer, string? text, CancellationToken ct = default);

    /// <summary>
    /// A new personal space owned by <paramref name="owner"/>, or null when the owner already has one with this name.
    /// Throws <see cref="ArgumentException"/> when the name isn't a valid container name or the owner has no id.
    /// </summary>
    Task<Container?> CreatePersonalAsync(MemoryCaller owner, string name, string? description, CancellationToken ct = default);

    /// <summary>What deleting the space would remove, counting every fact in it whoever can see them.</summary>
    Task<MemorySpaceImpact> ImpactAsync(Guid containerId, CancellationToken ct = default);

    /// <summary>Stars the space for <paramref name="caller"/> alone. Starring twice is the same as once.</summary>
    Task StarAsync(Guid containerId, MemoryCaller caller, CancellationToken ct = default);

    /// <summary>Removes <paramref name="caller"/>'s star, if there is one.</summary>
    Task UnstarAsync(Guid containerId, MemoryCaller caller, CancellationToken ct = default);
}

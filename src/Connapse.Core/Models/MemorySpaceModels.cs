namespace Connapse.Core;

/// <summary>
/// One place memory lives, as the Memory page lists it: <paramref name="Facts"/> counts only the current facts the
/// viewer may see, and <paramref name="UpdatedAt"/> is the later of the container's own change and the newest of those
/// facts, so a hidden fact never shows through either number.
/// </summary>
public sealed record MemorySpaceSummary(Container Container, int Facts, DateTimeOffset UpdatedAt, bool Starred);

/// <summary>
/// A space the viewer may see, with what is known before any source is checked. A current fact in the viewer's scope
/// that cites no source is always readable, so it sets the lower bounds; every current fact in their scope sets the
/// upper ones. The exact <see cref="MemorySpaceTally"/> lies between them.
/// </summary>
public sealed record MemorySpaceCandidate(
    Container Container,
    bool Starred,
    int FactsAtLeast,
    int FactsAtMost,
    DateTimeOffset UpdatedAtLeast,
    DateTimeOffset UpdatedAtMost)
{
    /// <summary>The bounds meet, so no source check can change the numbers.</summary>
    public bool Settled => FactsAtLeast == FactsAtMost && UpdatedAtLeast == UpdatedAtMost;
}

/// <summary>
/// A space's exact numbers for one viewer: the current facts they may read, and the later of the container's own change
/// and the newest of those facts, so a hidden fact never shows through either number.
/// </summary>
public sealed record MemorySpaceTally(int Facts, DateTimeOffset UpdatedAt);

/// <summary>What deleting a space removes: its facts (correction chains), their versions, and how many authors wrote them.</summary>
public sealed record MemorySpaceImpact(int Facts, int Versions, int Authors);

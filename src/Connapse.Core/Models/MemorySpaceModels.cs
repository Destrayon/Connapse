namespace Connapse.Core;

/// <summary>
/// One place memory lives, as the Memory page lists it: <paramref name="Facts"/> counts only the current facts the
/// viewer may see, and <paramref name="UpdatedAt"/> is the later of the container's own change and the newest of those
/// facts, so a hidden fact never shows through either number.
/// </summary>
public sealed record MemorySpaceSummary(Container Container, int Facts, DateTimeOffset UpdatedAt, bool Starred);

/// <summary>What deleting a space removes: its facts (correction chains), their versions, and how many authors wrote them.</summary>
public sealed record MemorySpaceImpact(int Facts, int Versions, int Authors);

namespace Connapse.Core;

/// <summary>A piece of text the link layer holds, and the document it came from.</summary>
public sealed record SourcedText(string Text, string DocumentId);

/// <summary>
/// Something links connect: a record, a code symbol, an entity an agent named. Each name and summary
/// keeps its own document, because one node can merge what several documents said about it.
/// </summary>
public sealed record LinkNode(string Id, string Kind, IReadOnlyList<SourcedText> Names, IReadOnlyList<SourcedText> Summaries);

/// <summary>
/// A typed link between two nodes. <paramref name="Evidence"/> is each document that supports it, with the
/// sentence it said (empty for a structural link such as "closes").
/// </summary>
public sealed record LinkEdge(string Id, string FromId, string ToId, string Type, IReadOnlyList<SourcedText> Evidence);

/// <summary>A walk outward from <paramref name="SeedIds"/>, at most <paramref name="MaxHops"/> links, in either direction.</summary>
public sealed record LinkExpandRequest(
    Guid? UserId,
    IReadOnlyList<string> SeedIds,
    int MaxHops,
    int MaxNodes = 100,
    IReadOnlyCollection<string>? EdgeTypes = null);

/// <summary>A node as one reader may see it: a name and summaries from readable documents only, and those documents.</summary>
public sealed record VisibleNode(string Id, string Kind, string Name, IReadOnlyList<SourcedText> Summaries, IReadOnlyList<string> Sources);

/// <summary>A link as one reader may see it: only the readable evidence, and those documents.</summary>
public sealed record VisibleEdge(string Id, string FromId, string ToId, string Type, IReadOnlyList<SourcedText> Evidence, IReadOnlyList<string> Sources);

/// <summary>
/// What a walk found. <paramref name="Denied"/> is true when access could not be checked, and then nothing
/// else is returned.
/// </summary>
public sealed record LinkExpansion(IReadOnlyList<VisibleNode> Nodes, IReadOnlyList<VisibleEdge> Edges, bool Denied = false)
{
    public static LinkExpansion DeniedResult { get; } = new([], [], Denied: true);
}

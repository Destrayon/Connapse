namespace Connapse.Core;

/// <summary>A piece of text the link layer holds, and the document it came from.</summary>
public sealed record SourcedText(string Text, string DocumentId);

/// <summary>
/// Something links connect: a record, a code symbol, an entity an agent named. Each kind, name and summary
/// keeps its own document, because one node can merge what several documents said about it, and what kind
/// of thing it is (a person, an acquisition target) can itself be private.
/// </summary>
public sealed record LinkNode(string Id, IReadOnlyList<SourcedText> Kinds, IReadOnlyList<SourcedText> Names, IReadOnlyList<SourcedText> Summaries);

/// <summary>
/// A typed link between two nodes. <paramref name="Evidence"/> is each document that supports it, with the
/// sentence it said (empty for a structural link such as "closes").
/// </summary>
public sealed record LinkEdge(string Id, string FromId, string ToId, string Type, IReadOnlyList<SourcedText> Evidence);

/// <summary>
/// A walk outward from <paramref name="SeedIds"/>, at most <paramref name="MaxHops"/> links, in either direction.
/// <paramref name="UserId"/> is the authenticated caller, set by the server as <c>SearchOptions.UserId</c> is,
/// never taken from a request body.
/// </summary>
public sealed record LinkExpandRequest(
    Guid? UserId,
    IReadOnlyList<string> SeedIds,
    int MaxHops,
    int MaxNodes = 100,
    IReadOnlyCollection<string>? EdgeTypes = null);

/// <summary>A node as one reader may see it: kinds, a name and summaries from readable documents only, and those documents.</summary>
public sealed record VisibleNode(string Id, IReadOnlyList<SourcedText> Kinds, string Name, IReadOnlyList<SourcedText> Summaries, IReadOnlyList<string> Sources);

/// <summary>
/// A link as one reader may see it: only the readable evidence, and those documents. <paramref name="Confidence"/> is
/// the highest any readable document gave it.
/// </summary>
public sealed record VisibleEdge(string Id, string FromId, string ToId, string Type, IReadOnlyList<SourcedText> Evidence, IReadOnlyList<string> Sources, double Confidence = 1.0);

/// <summary>
/// What a walk found. <paramref name="Denied"/> is true when access could not be checked, and then nothing
/// else is returned.
/// </summary>
public sealed record LinkExpansion(IReadOnlyList<VisibleNode> Nodes, IReadOnlyList<VisibleEdge> Edges, bool Denied = false)
{
    public static LinkExpansion DeniedResult { get; } = new([], [], Denied: true);
}

/// <summary>Which part of a node a <see cref="LinkFact"/> describes.</summary>
public enum LinkFactField { Kind = 0, Name = 1, Summary = 2 }

/// <summary>One thing a document says about a node: what kind of thing it is, a name for it, or a summary.</summary>
public sealed record LinkFact(string NodeId, LinkFactField Field, string Text);

/// <summary>
/// One link a document asserts. Several documents may assert the same <paramref name="EdgeId"/>; each keeps its own
/// <paramref name="Evidence"/> sentence (empty for a structural link such as "closes"). <paramref name="Confidence"/>,
/// from 0 to 1, is how sure the document is: 1 for a link it states, less for one inferred, such as a code call
/// matched to a definition by name.
/// </summary>
public sealed record LinkAssertion(string EdgeId, string FromId, string ToId, string Type, string Evidence = "", double Confidence = 1.0);

/// <summary>
/// Everything one document contributes to the link layer. Writing it replaces whatever that document said before.
/// <paramref name="NodeId"/> is the node the document itself stands for (an issue's own key), or null;
/// <paramref name="AlsoStandsFor"/> any further nodes it stands for, as a code file does for each definition in it.
/// </summary>
public sealed record DocumentLinks(
    string DocumentId,
    string? NodeId,
    IReadOnlyList<LinkFact> Facts,
    IReadOnlyList<LinkAssertion> Links,
    IReadOnlyList<string>? AlsoStandsFor = null);

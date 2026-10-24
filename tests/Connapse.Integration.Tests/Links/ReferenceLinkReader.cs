using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Integration.Tests.Links;

/// <summary>A leak planted on purpose, to prove the contract catches it.</summary>
public enum PlantedLeak
{
    None,
    /// <summary>Lists every evidence document and sentence of a visible link.</summary>
    AllEvidence,
    /// <summary>Names a merged node by its first name, readable or not.</summary>
    FirstName,
    /// <summary>Shows every kind a merged node was given, readable or not.</summary>
    AllKinds,
    /// <summary>Walks through hidden nodes and only drops them from the answer.</summary>
    WalkThroughHidden,
    /// <summary>Shows a link when both its ends are visible, whatever its evidence.</summary>
    LinkWithoutEvidence,
    /// <summary>Asks for each user's access once and keeps the answer.</summary>
    CachedAccess,
    /// <summary>Treats a failed permission check as "everything readable".</summary>
    FailOpen,
}

/// <summary>
/// An in-memory <see cref="ILinkReader"/> that keeps the rule: one access check per call, over every document
/// the world mentions, then a breadth-first walk over what that answer makes visible. It shows the contract
/// can be met, and with a <see cref="PlantedLeak"/> it is the canary that shows the contract can fail.
/// </summary>
public sealed class ReferenceLinkReader(LinkWorld world, IDocumentAccess access, PlantedLeak leak = PlantedLeak.None) : ILinkReader
{
    private readonly Dictionary<string, LinkNode> _nodes = world.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
    private readonly ILookup<string, LinkEdge> _touching = world.Edges
        .SelectMany(e => new[] { (Node: e.FromId, Edge: e), (Node: e.ToId, Edge: e) })
        .ToLookup(x => x.Node, x => x.Edge, StringComparer.Ordinal);
    private readonly string[] _documents = world.AllTexts.Select(t => t.DocumentId).Distinct(StringComparer.Ordinal).ToArray();
    private readonly Dictionary<string, IReadOnlySet<string>> _cache = new(StringComparer.Ordinal);

    public async Task<LinkExpansion> ExpandAsync(LinkExpandRequest request, CancellationToken ct = default)
    {
        IReadOnlySet<string> readable;
        try
        {
            string key = request.UserId?.ToString() ?? "anonymous";
            if (leak == PlantedLeak.CachedAccess && _cache.TryGetValue(key, out IReadOnlySet<string>? cached))
                readable = cached;
            else
                readable = _cache[key] = await access.ReadableAsync(request.UserId, _documents, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            if (leak != PlantedLeak.FailOpen)
                return LinkExpansion.DeniedResult;
            readable = _documents.ToHashSet(StringComparer.Ordinal);
        }

        bool Visible(string id) => _nodes.TryGetValue(id, out LinkNode? n) && n.Names.Any(x => readable.Contains(x.DocumentId));
        bool Usable(LinkEdge e) =>
            (request.EdgeTypes is null || request.EdgeTypes.Contains(e.Type))
            && (leak == PlantedLeak.LinkWithoutEvidence || e.Evidence.Any(x => readable.Contains(x.DocumentId)))
            && ((Visible(e.FromId) && Visible(e.ToId)) || leak == PlantedLeak.WalkThroughHidden);

        Dictionary<string, int> depth = new(StringComparer.Ordinal);
        Queue<string> queue = new();
        foreach (string seed in request.SeedIds.Where(Visible))
            if (depth.TryAdd(seed, 0))
                queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            string id = queue.Dequeue();
            if (depth[id] == request.MaxHops)
                continue;
            foreach (LinkEdge e in _touching[id].Where(Usable))
            {
                string other = e.FromId == id ? e.ToId : e.FromId;
                if (depth.TryAdd(other, depth[id] + 1))
                    queue.Enqueue(other);
            }
        }

        List<VisibleNode> nodes = depth.Keys
            .Where(Visible)
            .OrderBy(id => depth[id]).ThenBy(id => id, StringComparer.Ordinal)
            .Take(request.MaxNodes)
            .Select(id => Show(_nodes[id], readable))
            .ToList();
        HashSet<string> shown = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        List<VisibleEdge> edges = world.Edges
            .Where(e => shown.Contains(e.FromId) && shown.Contains(e.ToId) && Usable(e))
            .Select(e => Show(e, readable))
            .ToList();
        return new LinkExpansion(nodes, edges);
    }

    private VisibleNode Show(LinkNode node, IReadOnlySet<string> readable)
    {
        SourcedText name = leak == PlantedLeak.FirstName ? node.Names[0] : node.Names.First(n => readable.Contains(n.DocumentId));
        List<SourcedText> kinds = leak == PlantedLeak.AllKinds ? [.. node.Kinds] : [.. node.Kinds.Where(k => readable.Contains(k.DocumentId))];
        List<SourcedText> summaries = [.. node.Summaries.Where(s => readable.Contains(s.DocumentId))];
        List<string> sources = [.. node.Kinds.Concat(node.Names).Concat(node.Summaries).Select(t => t.DocumentId).Where(readable.Contains).Distinct()];
        return new VisibleNode(node.Id, kinds, name.Text, summaries, sources);
    }

    private VisibleEdge Show(LinkEdge edge, IReadOnlySet<string> readable)
    {
        List<SourcedText> evidence = leak == PlantedLeak.AllEvidence
            ? [.. edge.Evidence]
            : [.. edge.Evidence.Where(e => readable.Contains(e.DocumentId))];
        return new VisibleEdge(edge.Id, edge.FromId, edge.ToId, edge.Type, evidence,
            [.. evidence.Select(e => e.DocumentId).Distinct()]);
    }
}

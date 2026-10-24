using System.Text.Json;
using System.Text.RegularExpressions;
using Connapse.Core;

namespace Connapse.Integration.Tests.Links;

/// <summary>
/// The visibility rule written as plainly as possible, and every way an answer can break it. The expected
/// answer is worked out from the world and what the reader may read when the question was asked; the
/// reader under test never sees this code.
/// </summary>
public static partial class LinkLeakChecker
{
    public static bool NodeVisible(LinkNode node, IReadOnlySet<string> readable) =>
        node.Names.Any(n => readable.Contains(n.DocumentId));

    public static bool EdgeVisible(LinkEdge edge, IReadOnlyDictionary<string, LinkNode> nodes, IReadOnlySet<string> readable) =>
        edge.Evidence.Any(e => readable.Contains(e.DocumentId))
        && nodes.TryGetValue(edge.FromId, out LinkNode? from) && NodeVisible(from, readable)
        && nodes.TryGetValue(edge.ToId, out LinkNode? to) && NodeVisible(to, readable);

    /// <summary>Nodes a walk may reach: from the visible seeds, through visible links only, within the hop limit.</summary>
    public static HashSet<string> Reachable(LinkWorld world, LinkExpandRequest request, IReadOnlySet<string> readable)
    {
        Dictionary<string, LinkNode> nodes = world.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        List<LinkEdge> usable = world.Edges
            .Where(e => (request.EdgeTypes is null || request.EdgeTypes.Contains(e.Type)) && EdgeVisible(e, nodes, readable))
            .ToList();
        HashSet<string> reached = request.SeedIds
            .Where(id => nodes.TryGetValue(id, out LinkNode? n) && NodeVisible(n, readable))
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> frontier = [.. reached];
        for (int hop = 0; hop < request.MaxHops && frontier.Count > 0; hop++)
        {
            HashSet<string> next = new(StringComparer.Ordinal);
            foreach (LinkEdge e in usable)
            {
                if (frontier.Contains(e.FromId) && reached.Add(e.ToId)) next.Add(e.ToId);
                if (frontier.Contains(e.ToId) && reached.Add(e.FromId)) next.Add(e.FromId);
            }
            frontier = next;
        }
        return reached;
    }

    /// <summary>Every way <paramref name="answer"/> breaks the rule, as one line each; empty when it keeps it.</summary>
    public static List<string> Violations(
        LinkWorld world, LinkExpandRequest request, IReadOnlySet<string> readable, LinkExpansion answer, bool accessFailed = false)
    {
        List<string> v = [];
        if (accessFailed)
        {
            if (!answer.Denied || answer.Nodes.Count > 0 || answer.Edges.Count > 0)
                v.Add($"failed permission check: expected a denied, empty answer, got denied={answer.Denied}, {answer.Nodes.Count} nodes, {answer.Edges.Count} links");
            return v;
        }
        if (answer.Denied)
            v.Add("denied although the permission check answered");

        Dictionary<string, LinkNode> nodes = world.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        Dictionary<string, LinkEdge> edges = world.Edges.ToDictionary(e => e.Id, StringComparer.Ordinal);
        HashSet<string> reachable = Reachable(world, request, readable);
        HashSet<string> returned = new(StringComparer.Ordinal);

        foreach (VisibleNode shown in answer.Nodes)
        {
            if (!returned.Add(shown.Id))
                v.Add($"node {shown.Id} returned twice");
            if (!nodes.TryGetValue(shown.Id, out LinkNode? node))
            {
                v.Add($"node {shown.Id} does not exist");
                continue;
            }
            if (!NodeVisible(node, readable))
                v.Add($"node {shown.Id} shown with no readable name");
            else if (!reachable.Contains(shown.Id))
                v.Add($"node {shown.Id} is not reachable through visible nodes and links within {request.MaxHops} hops");
            if (!node.Names.Any(n => readable.Contains(n.DocumentId) && n.Text == shown.Name))
                v.Add($"node {shown.Id} name '{shown.Name}' is not a readable name");
            SameSet(v, $"node {shown.Id} kinds", shown.Kinds, node.Kinds.Where(k => readable.Contains(k.DocumentId)));
            SameSet(v, $"node {shown.Id} summaries", shown.Summaries, node.Summaries.Where(s => readable.Contains(s.DocumentId)));
            SameSet(v, $"node {shown.Id} sources", shown.Sources,
                node.Kinds.Concat(node.Names).Concat(node.Summaries).Select(t => t.DocumentId).Where(readable.Contains));
        }

        if (answer.Nodes.Count > request.MaxNodes)
            v.Add($"{answer.Nodes.Count} nodes returned, more than the {request.MaxNodes} asked for");
        else if (answer.Nodes.Count < request.MaxNodes && reachable.Except(returned).ToList() is { Count: > 0 } missing)
            v.Add($"walk incomplete: missing {string.Join(", ", missing.Order())}");

        HashSet<string> returnedEdges = new(StringComparer.Ordinal);
        foreach (VisibleEdge shown in answer.Edges)
        {
            if (!returnedEdges.Add(shown.Id))
                v.Add($"link {shown.Id} returned twice");
            if (!edges.TryGetValue(shown.Id, out LinkEdge? edge))
            {
                v.Add($"link {shown.Id} does not exist");
                continue;
            }
            if (!EdgeVisible(edge, nodes, readable))
                v.Add($"link {shown.Id} shown without readable evidence or with a hidden end");
            if (request.EdgeTypes is not null && !request.EdgeTypes.Contains(edge.Type))
                v.Add($"link {shown.Id} of type {edge.Type} was not asked for");
            if (!returned.Contains(edge.FromId) || !returned.Contains(edge.ToId))
                v.Add($"link {shown.Id} returned without both its ends");
            if (shown.FromId != edge.FromId || shown.ToId != edge.ToId || shown.Type != edge.Type)
                v.Add($"link {shown.Id} changed its ends or type");
            SameSet(v, $"link {shown.Id} evidence", shown.Evidence, edge.Evidence.Where(e => readable.Contains(e.DocumentId)));
            SameSet(v, $"link {shown.Id} sources", shown.Sources, edge.Evidence.Select(e => e.DocumentId).Where(readable.Contains));
        }

        // Words: anything in the answer, in any field, that came from a document the reader cannot open.
        Dictionary<string, string> tokenDocs = world.AllTexts
            .SelectMany(t => Token().Matches(t.Text).Select(m => (m.Value, t.DocumentId)))
            .ToDictionary(x => x.Value, x => x.DocumentId, StringComparer.Ordinal);
        string json = JsonSerializer.Serialize(answer);
        foreach (string token in Token().Matches(json).Select(m => m.Value).Distinct())
            if (tokenDocs.TryGetValue(token, out string? doc) && !readable.Contains(doc))
                v.Add($"words from unreadable {doc} in the answer ({token})");
        foreach (string doc in world.Documents.Where(d => !readable.Contains(d) && json.Contains($"\"{d}\"", StringComparison.Ordinal)))
            v.Add($"unreadable document {doc} named in the answer");
        return v;
    }

    private static void SameSet<T>(List<string> v, string what, IEnumerable<T> shown, IEnumerable<T> expected)
    {
        HashSet<T> a = [.. shown];
        HashSet<T> b = [.. expected];
        if (!a.SetEquals(b))
            v.Add($"{what}: shown {a.Count}, expected exactly the {b.Count} readable ones");
    }

    [GeneratedRegex(@"\bt\d+x\b")]
    private static partial Regex Token();
}

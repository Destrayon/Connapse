using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Links;

/// <summary>
/// Walks the link tables for one reader. One batched query per hop, and between hops the documents behind every
/// newly fetched name, kind, summary and link are checked with <paramref name="access"/>, so the next hop starts
/// only from nodes and links the reader may see. Nothing is cached across calls.
/// </summary>
public sealed class PostgresLinkReader(IDbContextFactory<KnowledgeDbContext> contextFactory, IDocumentAccess access) : ILinkReader
{
    public async Task<LinkExpansion> ExpandAsync(LinkExpandRequest request, CancellationToken ct = default)
    {
        try
        {
            return await WalkAsync(request, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A check that cannot answer has not said "readable". Nothing is shown rather than a guess.
            return LinkExpansion.DeniedResult;
        }
    }

    private async Task<LinkExpansion> WalkAsync(LinkExpandRequest request, CancellationToken ct)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);

        HashSet<string> readable = new(StringComparer.Ordinal);
        HashSet<string> asked = new(StringComparer.Ordinal);
        async Task CheckAsync(IEnumerable<string> documents)
        {
            List<string> fresh = documents.Where(asked.Add).ToList();
            readable.UnionWith(await access.ReadableAsync(request.UserId, fresh, ct));
        }

        Dictionary<string, List<LinkNodeFactEntity>> facts = new(StringComparer.Ordinal);
        async Task LoadFactsAsync(IEnumerable<string> nodes)
        {
            string[] missing = nodes.Where(n => !facts.ContainsKey(n)).Distinct(StringComparer.Ordinal).ToArray();
            if (missing.Length == 0)
                return;
            foreach (string id in missing)
                facts[id] = [];
            List<LinkNodeFactEntity> rows = await context.LinkNodeFacts.AsNoTracking()
                .Where(f => missing.Contains(f.NodeId))
                .ToListAsync(ct);
            foreach (LinkNodeFactEntity row in rows.OrderBy(r => r.DocumentId, StringComparer.Ordinal).ThenBy(r => r.Ordinal))
                facts[row.NodeId].Add(row);
        }

        bool Visible(string id) =>
            facts.TryGetValue(id, out List<LinkNodeFactEntity>? f)
            && f.Any(x => x.Field == (short)LinkFactField.Name && readable.Contains(x.DocumentId));

        string[] seeds = request.SeedIds.Distinct(StringComparer.Ordinal).ToArray();
        await LoadFactsAsync(seeds);
        // Asked even when empty: a failing check must deny the walk, not let an empty one through.
        await CheckAsync(seeds.SelectMany(s => facts[s]).Select(f => f.DocumentId));

        Dictionary<string, int> depth = new(StringComparer.Ordinal);
        foreach (string seed in seeds.Where(Visible))
            depth[seed] = 0;

        string[]? types = request.EdgeTypes?.ToArray();
        Dictionary<string, List<LinkEdgeEntity>> edges = new(StringComparer.Ordinal);
        HashSet<long> seenRows = [];
        List<string> frontier = [.. depth.Keys];

        for (int hop = 1; hop <= request.MaxHops && frontier.Count > 0; hop++)
        {
            string[] from = [.. frontier];
            IQueryable<LinkEdgeEntity> query = context.LinkEdges.AsNoTracking()
                .Where(e => from.Contains(e.FromNode) || from.Contains(e.ToNode));
            if (types is not null)
                query = query.Where(e => types.Contains(e.Type));
            List<LinkEdgeEntity> rows = await query.ToListAsync(ct);

            List<LinkEdgeEntity> fresh = rows.Where(r => seenRows.Add(r.Id)).ToList();
            foreach (LinkEdgeEntity row in fresh)
            {
                if (!edges.TryGetValue(row.EdgeId, out List<LinkEdgeEntity>? list))
                    edges[row.EdgeId] = list = [];
                list.Add(row);
            }

            await LoadFactsAsync(fresh.SelectMany(r => new[] { r.FromNode, r.ToNode }));
            await CheckAsync(fresh.Select(r => r.DocumentId)
                .Concat(fresh.SelectMany(r => facts[r.FromNode].Concat(facts[r.ToNode])).Select(f => f.DocumentId)));

            HashSet<string> current = [.. frontier];
            List<string> next = [];
            foreach (List<LinkEdgeEntity> edge in edges.Values)
            {
                if (!Usable(edge))
                    continue;
                LinkEdgeEntity e = edge[0];
                foreach ((string here, string there) in new[] { (e.FromNode, e.ToNode), (e.ToNode, e.FromNode) })
                {
                    if (current.Contains(here) && depth.TryAdd(there, hop))
                        next.Add(there);
                }
            }
            frontier = next;
        }

        bool Usable(List<LinkEdgeEntity> edge) =>
            edge.Any(r => readable.Contains(r.DocumentId)) && Visible(edge[0].FromNode) && Visible(edge[0].ToNode);

        List<VisibleNode> nodes = depth.Keys
            .OrderBy(id => depth[id]).ThenBy(id => id, StringComparer.Ordinal)
            .Take(request.MaxNodes)
            .Select(id => Show(id, facts[id], readable))
            .ToList();
        HashSet<string> shown = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        List<VisibleEdge> shownEdges = edges
            .Where(kv => Usable(kv.Value) && shown.Contains(kv.Value[0].FromNode) && shown.Contains(kv.Value[0].ToNode))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Show(kv.Key, kv.Value, readable))
            .ToList();

        return new LinkExpansion(nodes, shownEdges);
    }

    private static VisibleNode Show(string id, List<LinkNodeFactEntity> facts, IReadOnlySet<string> readable)
    {
        List<LinkNodeFactEntity> open = facts.Where(f => readable.Contains(f.DocumentId)).ToList();
        List<SourcedText> Of(LinkFactField field) =>
            [.. open.Where(f => f.Field == (short)field).Select(f => new SourcedText(f.Text, f.DocumentId))];

        return new VisibleNode(
            id,
            Of(LinkFactField.Kind),
            Of(LinkFactField.Name)[0].Text,
            Of(LinkFactField.Summary),
            [.. open.Select(f => f.DocumentId).Distinct(StringComparer.Ordinal)]);
    }

    private static VisibleEdge Show(string id, List<LinkEdgeEntity> rows, IReadOnlySet<string> readable)
    {
        List<SourcedText> evidence = [.. rows
            .Where(r => readable.Contains(r.DocumentId))
            .OrderBy(r => r.DocumentId, StringComparer.Ordinal).ThenBy(r => r.Ordinal)
            .Select(r => new SourcedText(r.Evidence, r.DocumentId))];
        LinkEdgeEntity first = rows[0];
        return new VisibleEdge(id, first.FromNode, first.ToNode, first.Type, evidence,
            [.. evidence.Select(e => e.DocumentId).Distinct(StringComparer.Ordinal)]);
    }
}

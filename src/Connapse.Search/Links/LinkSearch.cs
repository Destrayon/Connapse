using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Storage.Data;
using Connapse.Storage.Links;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Search.Links;

/// <summary>Scores chunks a search did not retrieve on the scale of the ones it did; missing chunks score nothing.</summary>
internal delegate Task<IReadOnlyDictionary<string, float>> ChunkScorer(IReadOnlyCollection<string> chunkIds, CancellationToken ct);

/// <summary>
/// The third search: walks the links of the first searches' top documents and blends what it reaches with them.
/// <para>
/// Seeds are the top <see cref="SearchSettings.LinkSeeds"/> documents. A document the walk reaches gets a link
/// score of its seed's normalised score halved per extra hop (HippoRAG's PPR damping of 0.5), the best over every
/// path. Reached documents not already retrieved are scored on the active searches, so their ordinary score is
/// real rather than zero. Every score is then (1 − w) × ordinary + w × link. When no seed stands for a node, or the
/// walk reaches nothing, the hits come back untouched: a search without links ranks exactly as before.
/// </para>
/// </summary>
internal sealed class LinkSearch(
    KnowledgeDbContext context,
    IDbContextFactory<KnowledgeDbContext> contextFactory,
    ILinkIndex linkIndex,
    ISearchResultVerifier verifier,
    ILogger logger)
{
    /// <summary>Most reached documents scored and added per search.</summary>
    internal const int MaxReachedDocuments = 20;

    /// <summary>The link score lost per hop beyond the first.</summary>
    internal const float HopDecay = 0.5f;

    /// <summary>
    /// The share of the link score a light link passes on: a Confluence page link or shared label is weaker evidence
    /// that two pages answer one question than a stated relation such as "blocks" or "parent" (CON-34).
    /// </summary>
    internal const float LightLinkWeight = 0.5f;

    public async Task<List<SearchHit>> ApplyAsync(
        List<SearchHit> hits,
        SearchSettings settings,
        SearchOptions options,
        SearchScopes scopes,
        ChunkScorer scoreChunks,
        CancellationToken ct)
    {
        if (settings.LinkWeight <= 0 || hits.Count == 0)
            return hits;

        Dictionary<string, float> seedScores = SeedScores(hits, settings.LinkSeeds);
        ILookup<string, string> seedNodes = await linkIndex.NodesOfAsync(seedScores.Keys, ct);
        if (!seedNodes.Any())
            return hits;

        // Hits are not verified yet: a seed the per-hit verifier would drop must not lend its score to anything.
        SearchDocumentAccess access = new(context, verifier, options, scopes);
        IReadOnlySet<string> readableSeeds = await access.ReadableAsync(
            options.UserId, [.. seedNodes.Select(g => g.Key)], ct);
        seedNodes = seedNodes.Where(g => readableSeeds.Contains(g.Key))
            .SelectMany(g => g.Select(node => (Doc: g.Key, Node: node)))
            .ToLookup(x => x.Doc, x => x.Node, StringComparer.Ordinal);
        if (!seedNodes.Any())
            return hits;
        Dictionary<string, float> nodeSeeds = new(StringComparer.Ordinal);
        foreach (IGrouping<string, string> doc in seedNodes)
            foreach (string node in doc)
                nodeSeeds[node] = Math.Max(nodeSeeds.GetValueOrDefault(node), seedScores[doc.Key]);

        LinkExpansion walk = await new PostgresLinkReader(contextFactory, access).ExpandAsync(
            new LinkExpandRequest(options.UserId, [.. nodeSeeds.Keys], Math.Clamp(settings.LinkMaxHops, 1, 2)), ct);
        if (walk.Denied)
        {
            logger.LogWarning("Link search could not check access; returning the other searches' results alone");
            return hits;
        }

        Dictionary<string, float> nodeScores = PropagateScores(nodeSeeds, walk, Math.Clamp(settings.LinkMaxHops, 1, 2));
        if (nodeScores.Count == 0)
            return hits;

        // A node's own document must itself be readable before it is scored or shown.
        ILookup<string, string> nodeDocs = await linkIndex.DocumentsOfAsync(nodeScores.Keys, ct);
        Dictionary<string, float> docScores = new(StringComparer.Ordinal);
        foreach (IGrouping<string, string> node in nodeDocs)
            foreach (string doc in node)
                docScores[doc] = Math.Max(docScores.GetValueOrDefault(doc), nodeScores[node.Key]);
        if (docScores.Count == 0)
            return hits;

        IReadOnlySet<string> readable = await access.ReadableAsync(options.UserId, docScores.Keys, ct);
        List<KeyValuePair<string, float>> candidates = [.. docScores.Where(d => readable.Contains(d.Key))];
        Dictionary<string, float> reached = TopReached(
            candidates, await PathsAsync([.. candidates.Select(d => d.Key)], ct), MaxReachedDocuments);
        if (reached.Count == 0)
            return hits;

        HashSet<string> retrieved = hits.Select(h => h.DocumentId).ToHashSet(StringComparer.Ordinal);
        List<SearchHit> extras = await BestChunksAsync(
            [.. reached.Keys.Where(d => !retrieved.Contains(d))], scoreChunks, ct);

        logger.LogDebug("Link search reached {Reached} documents, {New} not retrieved by the other searches",
            reached.Count, extras.Count);

        return Blend(hits, extras, reached, settings.LinkWeight);
    }

    /// <summary>The top documents' scores, min-max normalised over the hits (all equal: 1).</summary>
    internal static Dictionary<string, float> SeedScores(List<SearchHit> hits, int seeds)
    {
        float max = hits.Max(h => h.Score), min = hits.Min(h => h.Score);
        float range = max - min;
        return hits
            .OrderByDescending(h => h.Score)
            .GroupBy(h => h.DocumentId, StringComparer.Ordinal)
            .Take(Math.Max(1, seeds))
            .ToDictionary(g => g.Key, g => range > 0 ? (g.First().Score - min) / range : 1f, StringComparer.Ordinal);
    }

    /// <summary>
    /// Each node the walk reached, beyond the seeds, with the best of its seeds' scores times
    /// <see cref="HopDecay"/> per hop after the first, and <see cref="LightLinkWeight"/> per light link on the path.
    /// Seeds reached from another seed keep the better of the two.
    /// </summary>
    internal static Dictionary<string, float> PropagateScores(
        IReadOnlyDictionary<string, float> seeds, LinkExpansion walk, int maxHops)
    {
        HashSet<string> visible = walk.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        ILookup<string, (string Node, float Weight)> neighbours = walk.Edges
            .SelectMany(e =>
            {
                float weight = ConfluenceLinks.LightTypes.Contains(e.Type) ? LightLinkWeight : 1f;
                return new[] { (e.FromId, (e.ToId, weight)), (e.ToId, (e.FromId, weight)) };
            })
            .ToLookup(x => x.Item1, x => x.Item2, StringComparer.Ordinal);

        // Paths carry the seed they started from, so a seed that walks out and back never scores itself.
        Dictionary<string, float> scores = new(StringComparer.Ordinal);
        List<(string Node, string Seed, float Score)> frontier = seeds
            .Where(s => visible.Contains(s.Key))
            .Select(s => (s.Key, s.Key, s.Value))
            .ToList();
        float factor = 1f;
        for (int hop = 1; hop <= maxHops && frontier.Count > 0; hop++)
        {
            List<(string Node, string Seed, float Score)> next = [];
            foreach ((string node, string seed, float score) in frontier)
                foreach ((string other, float weight) in neighbours[node])
                {
                    if (other == seed)
                        continue;
                    float value = score * weight * factor;
                    if (value > scores.GetValueOrDefault(other, -1f))
                        scores[other] = value;
                    next.Add((other, seed, score * weight));
                }

            // Each node walks on once per seed, with that seed's score.
            frontier = next.GroupBy(x => (x.Node, x.Seed)).Select(g => g.MaxBy(x => x.Score)).ToList();
            factor *= HopDecay;
        }
        return scores;
    }

    /// <summary>
    /// The <paramref name="max"/> best-scored reached documents. Ties go to the earlier path, then the earlier id
    /// (CON-152).
    /// </summary>
    internal static Dictionary<string, float> TopReached(
        IEnumerable<KeyValuePair<string, float>> scores, IReadOnlyDictionary<string, string> paths, int max) =>
        scores
            .OrderByDescending(d => d.Value)
            .ThenBy(d => paths.GetValueOrDefault(d.Key, ""), StringComparer.Ordinal)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .Take(max)
            .ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);

    /// <summary>
    /// Every score becomes (1 − w) × its ordinary score, min-max normalised over hits and extras, plus w × its
    /// document's link score. Extras are tagged as found by links. Equal scores go to the earlier path, then the
    /// earlier chunk, then the chunk id, so the order does not hang on the order the searches returned (CON-152).
    /// </summary>
    internal static List<SearchHit> Blend(
        List<SearchHit> hits, List<SearchHit> extras, IReadOnlyDictionary<string, float> linkScores, float weight)
    {
        List<SearchHit> all = [.. hits, .. extras];
        float max = all.Max(h => h.Score), min = all.Min(h => h.Score);
        float range = max - min;
        float w = Math.Clamp(weight, 0f, 1f);
        HashSet<string> added = extras.Select(e => e.ChunkId).ToHashSet(StringComparer.Ordinal);

        return all
            .Select(h =>
            {
                float baseScore = range > 0 ? (h.Score - min) / range : (h.Score > 0 ? 1f : 0f);
                float link = linkScores.GetValueOrDefault(h.DocumentId);
                if (link <= 0 && !added.Contains(h.ChunkId))
                    return h with { Score = (1 - w) * baseScore };

                var metadata = new Dictionary<string, string>(h.Metadata) { ["linkScore"] = link.ToString("F4") };
                if (added.Contains(h.ChunkId))
                    metadata["source"] = "links";
                return h with { Score = (1 - w) * baseScore + w * link, Metadata = metadata };
            })
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Metadata.GetValueOrDefault("path", ""), StringComparer.Ordinal)
            .ThenBy(h => int.TryParse(h.Metadata.GetValueOrDefault("chunkIndex"), out int index) ? index : int.MaxValue)
            .ThenBy(h => h.ChunkId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>For each document, its chunk the active searches score highest (its first chunk when none scores).</summary>
    private async Task<List<SearchHit>> BestChunksAsync(List<string> documentIds, ChunkScorer scoreChunks, CancellationToken ct)
    {
        Guid[] ids = documentIds.Select(Guid.Parse).ToArray();
        if (ids.Length == 0)
            return [];

        List<ChunkRow> chunks = await context.Database
            .SqlQueryRaw<ChunkRow>(
                """
                SELECT c.id AS "ChunkId", c.document_id AS "DocumentId", c.content AS "Content",
                       c.chunk_index AS "ChunkIndex", c.metadata::text AS "MetadataJson", d.file_name AS "FileName",
                       d.content_type AS "ContentType", d.owner_id AS "ContainerId", d.path AS "Path"
                FROM chunks c
                INNER JOIN documents d ON c.document_id = d.id
                WHERE c.document_id = ANY({0})
                """, [ids])
            .ToListAsync(ct);
        if (chunks.Count == 0)
            return [];

        IReadOnlyDictionary<string, float> scores = await scoreChunks([.. chunks.Select(c => c.ChunkId.ToString())], ct);
        return BestChunks(chunks, scores);
    }

    /// <summary>
    /// Each document's highest-scored chunk (its first when none scores), in path order: the blend keeps this order
    /// among equal scores, and the rows arrive in whatever order the database returns them.
    /// </summary>
    internal static List<SearchHit> BestChunks(IEnumerable<ChunkRow> chunks, IReadOnlyDictionary<string, float> scores) =>
        chunks
            .GroupBy(c => c.DocumentId)
            .Select(g => g
                .OrderByDescending(c => scores.GetValueOrDefault(c.ChunkId.ToString()))
                .ThenBy(c => c.ChunkIndex)
                .First())
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .ThenBy(c => c.DocumentId)
            .Select(c => new SearchHit(
                ChunkId: c.ChunkId.ToString(),
                DocumentId: c.DocumentId.ToString(),
                Content: c.Content,
                Score: scores.GetValueOrDefault(c.ChunkId.ToString()),
                Metadata: Metadata(c)))
            .ToList();

    /// <summary>Each document's path, for breaking ties by a key that survives re-indexing.</summary>
    private async Task<Dictionary<string, string>> PathsAsync(List<string> documentIds, CancellationToken ct)
    {
        Guid[] ids = documentIds.Select(Guid.Parse).ToArray();
        if (ids.Length == 0)
            return [];

        List<DocumentPath> rows = await context.Database
            .SqlQueryRaw<DocumentPath>(
                """SELECT d.id AS "Id", d.path AS "Path" FROM documents d WHERE d.id = ANY({0})""", [ids])
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id.ToString(), r => r.Path, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> Metadata(ChunkRow c)
    {
        Dictionary<string, string> metadata = new();
        if (!string.IsNullOrEmpty(c.MetadataJson))
        {
            try
            {
                foreach ((string key, string value) in JsonSerializer.Deserialize<Dictionary<string, string>>(c.MetadataJson) ?? [])
                    metadata[key] = value;
            }
            catch (JsonException)
            {
                // A chunk whose metadata is not flat strings is shown without it.
            }
        }
        metadata["documentId"] = c.DocumentId.ToString();
        metadata["fileName"] = c.FileName;
        metadata["contentType"] = c.ContentType ?? "";
        metadata["containerId"] = c.ContainerId.ToString();
        metadata["chunkIndex"] = c.ChunkIndex.ToString();
        metadata["path"] = c.Path;
        return metadata;
    }

    private sealed class DocumentPath
    {
        public Guid Id { get; init; }
        public string Path { get; init; } = "";
    }

    internal sealed class ChunkRow
    {
        public Guid ChunkId { get; init; }
        public Guid DocumentId { get; init; }
        public string Content { get; init; } = "";
        public int ChunkIndex { get; init; }
        public string? MetadataJson { get; init; }
        public string FileName { get; init; } = "";
        public string? ContentType { get; init; }
        public Guid ContainerId { get; init; }
        public string Path { get; init; } = "";
    }
}

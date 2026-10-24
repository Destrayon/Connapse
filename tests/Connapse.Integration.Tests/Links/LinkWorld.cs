using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Integration.Tests.Links;

/// <summary>
/// Everything the link layer stores for one test: nodes, links, and which users may read which documents.
/// Every name, summary and evidence sentence is made of tokens (<c>t17x</c>) that appear nowhere else, so a
/// token in a reader's answer names exactly the document it came from.
/// </summary>
public sealed record LinkWorld(
    IReadOnlyList<LinkNode> Nodes,
    IReadOnlyList<LinkEdge> Edges,
    IReadOnlyList<string> Documents,
    IReadOnlyList<Guid?> Users)
{
    public IEnumerable<SourcedText> AllTexts =>
        Nodes.SelectMany(n => n.Kinds.Concat(n.Names).Concat(n.Summaries)).Concat(Edges.SelectMany(e => e.Evidence));

    /// <summary>
    /// Seeded random worlds: document nodes (a record names and summarises itself), entity nodes whose kinds,
    /// names and summaries are merged from several documents, and links whose evidence comes from one to three documents. Users read random
    /// subsets, one of them nothing at all.
    /// </summary>
    public static (LinkWorld World, DocumentAccessStub Access) Generate(int seed)
    {
        Random rng = new(seed);
        int token = 0;
        string Text(int words) => string.Join(' ', Enumerable.Range(0, words).Select(_ => $"t{++token}x"));
        SourcedText From(string doc, int words = 2) => new(Text(words), doc);

        List<string> docs = [.. Enumerable.Range(0, rng.Next(6, 16)).Select(i => $"doc-{seed}-{i}")];
        string AnyDoc() => docs[rng.Next(docs.Count)];

        List<LinkNode> nodes = [.. docs.Select(d => new LinkNode($"n:{d}", [new SourcedText("record", d)], [From(d)], [From(d, 4)]))];
        int entities = rng.Next(2, 8);
        for (int i = 0; i < entities; i++)
        {
            List<SourcedText> names = [.. Enumerable.Range(0, rng.Next(1, 4)).Select(_ => From(AnyDoc()))];
            List<SourcedText> summaries = [.. Enumerable.Range(0, rng.Next(0, 4)).Select(_ => From(AnyDoc(), 3))];
            List<SourcedText> kinds = [.. Enumerable.Range(0, rng.Next(0, 3)).Select(_ => From(AnyDoc(), 1))];
            nodes.Add(new LinkNode($"e:{seed}-{i}", kinds, names, summaries));
        }

        string[] types = ["closes", "blocks", "mentions", "parent"];
        List<LinkEdge> edges = [];
        int edgeCount = rng.Next(nodes.Count, nodes.Count * 3);
        for (int i = 0; i < edgeCount; i++)
        {
            LinkNode from = nodes[rng.Next(nodes.Count)];
            LinkNode to = nodes[rng.Next(nodes.Count)];
            if (from.Id == to.Id)
                continue;
            // Most evidence is the documents at either end; some comes from a third document.
            List<SourcedText> evidence = [];
            int count = rng.Next(1, 4);
            for (int k = 0; k < count; k++)
            {
                string doc = rng.Next(3) switch
                {
                    0 when from.Id.StartsWith("n:", StringComparison.Ordinal) => from.Names[0].DocumentId,
                    1 when to.Id.StartsWith("n:", StringComparison.Ordinal) => to.Names[0].DocumentId,
                    _ => AnyDoc(),
                };
                evidence.Add(new SourcedText(rng.Next(2) == 0 ? "" : Text(3), doc));
            }
            edges.Add(new LinkEdge($"l:{seed}-{i}", from.Id, to.Id, types[rng.Next(types.Length)], evidence));
        }

        List<Guid?> users = [Guid.NewGuid(), Guid.NewGuid(), null];
        DocumentAccessStub access = new();
        foreach (Guid? user in users.Take(2))
            access.Grant(user, docs.Where(_ => rng.NextDouble() < 0.6));
        // The anonymous reader reads nothing; a walk for it must come back empty but not denied.
        return (new LinkWorld(nodes, edges, docs, users), access);
    }
}

/// <summary>An access answer the test controls: grants per user, revocation between calls, and failure.</summary>
public sealed class DocumentAccessStub : IDocumentAccess
{
    private readonly Dictionary<string, HashSet<string>> _grants = new(StringComparer.Ordinal);

    public bool Fail { get; set; }

    public int Calls { get; private set; }

    private static string Key(Guid? user) => user?.ToString() ?? "anonymous";

    public void Grant(Guid? user, IEnumerable<string> documents)
    {
        if (!_grants.TryGetValue(Key(user), out HashSet<string>? set))
            _grants[Key(user)] = set = new HashSet<string>(StringComparer.Ordinal);
        set.UnionWith(documents);
    }

    public void Revoke(Guid? user, string document) =>
        _grants.GetValueOrDefault(Key(user))?.Remove(document);

    /// <summary>What the user may read now, for the checker; not counted as a call.</summary>
    public IReadOnlySet<string> Readable(Guid? user) =>
        _grants.TryGetValue(Key(user), out HashSet<string>? set) ? set.ToHashSet(StringComparer.Ordinal) : new HashSet<string>();

    public Task<IReadOnlySet<string>> ReadableAsync(Guid? userId, IReadOnlyCollection<string> documentIds, CancellationToken ct = default)
    {
        Calls++;
        if (Fail)
            throw new InvalidOperationException("The permission check is unavailable.");
        IReadOnlySet<string> readable = Readable(userId);
        return Task.FromResult<IReadOnlySet<string>>(documentIds.Where(readable.Contains).ToHashSet(StringComparer.Ordinal));
    }
}

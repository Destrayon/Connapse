using System.Globalization;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Links;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Search.Code;

/// <summary>
/// The code questions agents ask (CON-37), answered from the links code ingestion writes (CON-109) and from the code's
/// own text where no link reaches.
/// <para>
/// A code node id is <c>code:&lt;owner&gt;:&lt;language&gt;:&lt;key&gt;</c>, the key being the qualified name, so a name
/// is found by matching the end of the key. Each link row names the document that asserted it: a call's row is the
/// calling file, a "defines" row the defining file. Every document an answer would show is checked with the same rule
/// and verifier search uses, scoped to the one container or source asked about. A check that throws denies the whole
/// answer rather than guess.
/// </para>
/// </summary>
public sealed partial class CodeTools(
    IDbContextFactory<KnowledgeDbContext> contextFactory,
    ISearchScopeResolver scopeResolver,
    ISearchResultVerifier verifier,
    IKnowledgeSearch search,
    ILogger<CodeTools> logger) : ICodeTools
{
    internal const string Defines = "defines";
    internal const string Calls = "calls";
    internal const string Inherits = "inherits";
    internal const string Imports = "imports";

    /// <summary>The most identifiers of a body looked up when following what it uses.</summary>
    private const int MaxBodyNames = 80;

    /// <summary>A name with more definitions than this says too little about which one a body means.</summary>
    private const int MaxNameCandidates = 4;

    public Task<CodeSymbolResult> FindSymbolAsync(CodeScope scope, string query, CodeMatch match, bool follow, int limit, CancellationToken ct = default) =>
        GuardAsync(scope, CodeSymbolResult.DeniedResult, (call, c) => FindAsync(call, query, match, follow, Math.Clamp(limit, 1, 100), c), ct);

    public Task<CodeReferencesResult> ReferencesAsync(CodeScope scope, string symbol, CodeReferenceKind kind, int limit, CancellationToken ct = default) =>
        GuardAsync(scope, CodeReferencesResult.DeniedResult, (call, c) => ReferencesCoreAsync(call, symbol, kind, Math.Clamp(limit, 1, 200), c), ct);

    public Task<CodeImpactResult> ImpactAsync(CodeScope scope, string symbol, int depth, int limit, CancellationToken ct = default) =>
        GuardAsync(scope, CodeImpactResult.DeniedResult, (call, c) => ImpactCoreAsync(call, symbol, Math.Clamp(depth, 1, 3), Math.Clamp(limit, 1, 200), c), ct);

    public Task<CodeRepoMap> RepoMapAsync(CodeScope scope, string? pathPrefix, int maxFiles, CancellationToken ct = default) =>
        GuardAsync(scope, CodeRepoMap.DeniedResult, (call, c) => RepoMapCoreAsync(call, pathPrefix, Math.Clamp(maxFiles, 1, 200), c), ct);

    // ---- one call ------------------------------------------------------------------------------------------------

    /// <summary>A database context and the caller's access, for the length of one tool call.</summary>
    private sealed class Call(KnowledgeDbContext context, CodeScope scope, IDocumentAccess access)
    {
        private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
        private readonly HashSet<string> _readable = new(StringComparer.Ordinal);

        public KnowledgeDbContext Context => context;
        public CodeScope Scope => scope;

        /// <summary>The owner segment of this scope's code node ids, once a definition has been read.</summary>
        public string? OwnerSegment { get; set; }

        public async Task<IReadOnlySet<string>> ReadableAsync(IEnumerable<string> documentIds, CancellationToken ct)
        {
            List<string> fresh = documentIds.Where(_asked.Add).ToList();
            if (fresh.Count > 0)
                _readable.UnionWith(await access.ReadableAsync(scope.UserId, fresh, ct));
            return _readable;
        }
    }

    private async Task<T> GuardAsync<T>(CodeScope scope, T denied, Func<Call, CancellationToken, Task<T>> run, CancellationToken ct)
    {
        try
        {
            SearchScopes scopes;
            try
            {
                scopes = await scopeResolver.ResolveAsync(scope.UserId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                scopes = SearchScopes.Failed;
            }
            scopes = ScopeResolution.Guard(scopes, scope.UserId);

            await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
            var access = new SearchDocumentAccess(context, verifier,
                new SearchOptions(ContainerId: scope.OwnerId.ToString(), UserId: scope.UserId), scopes);
            return await run(new Call(context, scope, access), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A check that cannot answer has not said "readable": nothing is shown rather than a guess.
            logger.LogWarning(ex, "A code question could not be answered; returning nothing");
            return denied;
        }
    }

    // ---- find ----------------------------------------------------------------------------------------------------

    private async Task<CodeSymbolResult> FindAsync(Call call, string query, CodeMatch match, bool follow, int limit, CancellationToken ct)
    {
        List<CodeDefinition> definitions = match switch
        {
            CodeMatch.Words => await DefinitionsAsync(call, await NodesByWordsAsync(call, query, limit * 4, ct), ct),
            CodeMatch.Meaning => await DefinitionsByMeaningAsync(call, query, limit, ct),
            _ => await DefinitionsAsync(call, await NodesByNameAsync(call, query, ct), ct),
        };
        definitions = [.. definitions.Take(limit)];
        if (definitions.Count == 0)
        {
            // Fields, constants, properties and type aliases have no node of their own; their declarations are still text.
            if (match != CodeMatch.Exact)
                return new CodeSymbolResult([], [], []);
            List<CodeFileMatch> declared = await DeclaredInAsync(call, query, ct);
            List<CodeFileMatch> mentioned = follow && declared.Count > 0 ? await NamedInAsync(call, Normalize(query), ct) : [];
            return new CodeSymbolResult([], [], [.. Merge(declared, mentioned).Take(limit)]);
        }

        List<CodeFileMatch> files = [.. definitions
            .GroupBy(d => d.DocumentId, StringComparer.Ordinal)
            .Select(g => new CodeFileMatch(g.First().Path, g.Key, 3.0, "defines it",
                [.. g.Select(d => new CodeLine(d.StartLine, d.Signature ?? d.Symbol))]))];
        if (!follow)
            return new CodeSymbolResult(definitions, [], files);

        CodeDefinition first = definitions[0];
        List<CodeDefinition> same = [.. definitions.Where(d => d.Symbol == first.Symbol && d.Language == first.Language)];
        (List<CodeUse> uses, List<CodeFileMatch> used) = await UsesAsync(call, same, ct);
        List<CodeFileMatch> named = await BodyNamesAsync(call, same, ct);

        return new CodeSymbolResult(definitions, uses, Merge(files, used, named));
    }

    /// <summary>The code nodes whose key is <paramref name="query"/> or ends with it after a '.', '#' or ':'.</summary>
    private static async Task<List<string>> NodesByNameAsync(Call call, string query, CancellationToken ct)
    {
        string name = Normalize(query);
        if (name.Length == 0)
            return [];
        string[] endings = [$".{name}", $"#{name}", $":{name}"];
        List<NodeRow> rows = await call.Context.Database.SqlQueryRaw<NodeRow>(
            """
            SELECT DISTINCT f.node_id AS "NodeId"
            FROM link_node_facts f
            INNER JOIN documents d ON d.id::text = f.document_id
            WHERE d.owner_id = {0} AND f.field = 0 AND f.node_id LIKE 'code:%'
              AND f.text NOT IN ('file', 'namespace')
              AND right(f.node_id, {1}) = ANY({2})
            """, call.Scope.OwnerId, name.Length + 1, endings).ToListAsync(ct);
        return [.. rows.Select(r => r.NodeId)];
    }

    /// <summary>The code nodes whose key holds every word of <paramref name="query"/>, shortest keys first.</summary>
    private static async Task<List<string>> NodesByWordsAsync(Call call, string query, int limit, CancellationToken ct)
    {
        string[] words = [.. WordPattern().Matches(query).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(8)];
        if (words.Length == 0)
            return [];
        List<string> where = [];
        List<object> parameters = [call.Scope.OwnerId];
        foreach (string word in words)
        {
            where.Add($"f.node_id ILIKE {{{parameters.Count}}}");
            parameters.Add("%" + EscapeLike(word) + "%");
        }
        // Only placeholders and fixed SQL are spliced in; every word is a parameter.
        string sql = $$"""
            SELECT DISTINCT f.node_id AS "NodeId"
            FROM link_node_facts f
            INNER JOIN documents d ON d.id::text = f.document_id
            WHERE d.owner_id = {0} AND f.field = 0 AND f.node_id LIKE 'code:%'
              AND f.text NOT IN ('file', 'namespace') AND {{string.Join(" AND ", where)}}
            """;
        List<NodeRow> rows = await call.Context.Database.SqlQueryRaw<NodeRow>(sql, [.. parameters]).ToListAsync(ct);

        // A word matching the symbol's own name counts most, then the shorter key: less around it that merely matched.
        return [.. rows.Select(r => r.NodeId)
            .OrderByDescending(n => words.Count(w => LastSegment(Key(n)).Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(n => Key(n).Length)
            .ThenBy(n => n, StringComparer.Ordinal)
            .Take(limit)];
    }

    /// <summary>Definitions whose code the container's search ranks highest for <paramref name="query"/>.</summary>
    private async Task<List<CodeDefinition>> DefinitionsByMeaningAsync(Call call, string query, int limit, CancellationToken ct)
    {
        SearchResult result = await search.SearchAsync(query,
            new SearchOptions(TopK: Math.Min(100, limit * 4), ContainerId: call.Scope.OwnerId.ToString(), UserId: call.Scope.UserId), ct);

        // Each hit names the definition its passage was cut from; the search has already checked the caller may read it.
        List<(string Document, string Symbol)> wanted = [.. result.Hits
            .Select(h => (h.DocumentId, Symbol: h.Metadata.GetValueOrDefault("CodeSymbol") ?? ""))
            .Where(h => h.Symbol.Length > 0 && !h.Symbol.Contains(':'))
            .Distinct()];
        if (wanted.Count == 0)
            return [];

        string[] documents = [.. wanted.Select(w => w.Document).Distinct(StringComparer.Ordinal)];
        List<EdgeRow> defines = await call.Context.Database.SqlQueryRaw<EdgeRow>(
            """
            SELECT e.from_node AS "FromNode", e.to_node AS "ToNode", e.type AS "Type", e.evidence AS "Evidence",
                   e.confidence AS "Confidence", e.document_id AS "DocumentId", d.path AS "Path"
            FROM link_edges e
            INNER JOIN documents d ON d.id::text = e.document_id
            WHERE d.owner_id = {0} AND e.type = 'defines' AND e.document_id = ANY({1})
            """, call.Scope.OwnerId, documents).ToListAsync(ct);

        List<string> nodes = [];
        foreach ((string document, string symbol) in wanted)
        {
            string? node = defines.Where(e => e.DocumentId == document && KeyEndsWith(Key(e.ToNode), symbol))
                .Select(e => e.ToNode).OrderBy(n => n.Length).FirstOrDefault();
            if (node is not null && !nodes.Contains(node))
                nodes.Add(node);
        }
        List<CodeDefinition> found = await DefinitionsAsync(call, nodes, ct);
        return [.. found.OrderBy(d => nodes.IndexOf(Node(d, nodes)))];
    }

    private static string Node(CodeDefinition definition, List<string> nodes) =>
        nodes.FirstOrDefault(n => Key(n) == definition.Symbol && Language(n) == definition.Language) ?? "";

    /// <summary>
    /// Every readable definition of <paramref name="nodes"/>: the file, line and signature each defining document gave,
    /// types before members, then by key.
    /// </summary>
    private static async Task<List<CodeDefinition>> DefinitionsAsync(Call call, List<string> nodes, CancellationToken ct)
    {
        if (nodes.Count == 0)
            return [];
        string[] ids = [.. nodes.Distinct(StringComparer.Ordinal)];
        List<EdgeRow> defines = await call.Context.Database.SqlQueryRaw<EdgeRow>(
            """
            SELECT e.from_node AS "FromNode", e.to_node AS "ToNode", e.type AS "Type", e.evidence AS "Evidence",
                   e.confidence AS "Confidence", e.document_id AS "DocumentId", d.path AS "Path"
            FROM link_edges e
            INNER JOIN documents d ON d.id::text = e.document_id
            WHERE d.owner_id = {0} AND e.type = 'defines' AND e.to_node = ANY({1})
            """, call.Scope.OwnerId, ids).ToListAsync(ct);
        IReadOnlySet<string> readable = await call.ReadableAsync(defines.Select(e => e.DocumentId), ct);
        defines = [.. defines.Where(e => readable.Contains(e.DocumentId))];
        if (defines.Count == 0)
            return [];
        call.OwnerSegment ??= OwnerSegmentOf(defines[0].ToNode);

        List<FactRow> facts = await call.Context.Database.SqlQueryRaw<FactRow>(
            """
            SELECT f.node_id AS "NodeId", f.field AS "Field", f.text AS "Text", f.document_id AS "DocumentId"
            FROM link_node_facts f
            WHERE f.node_id = ANY({0}) AND f.field IN (0, 2) AND f.document_id = ANY({1})
            """, ids, defines.Select(e => e.DocumentId).Distinct(StringComparer.Ordinal).ToArray()).ToListAsync(ct);

        Dictionary<string, Shape> shapes = await ShapesAsync(call, defines.Select(e => e.DocumentId), withText: false, ct);

        return [.. defines
            .Select(e =>
            {
                string kind = facts.FirstOrDefault(f => f.NodeId == e.ToNode && f.DocumentId == e.DocumentId && f.Field == 0)?.Text ?? "symbol";
                string? signature = facts.FirstOrDefault(f => f.NodeId == e.ToNode && f.DocumentId == e.DocumentId && f.Field == 2)?.Text;
                int line = LineOf(e.Evidence);
                (int? end, _) = Body(shapes.GetValueOrDefault(e.DocumentId) ?? Shape.Empty, Key(e.ToNode), line);
                return new CodeDefinition(Key(e.ToNode), kind, Language(e.ToNode), e.Path, line, end, signature, e.DocumentId);
            })
            .DistinctBy(d => (d.Symbol, d.Language, d.DocumentId))
            .OrderBy(d => IsTypeKind(d.Kind) ? 0 : 1)
            .ThenBy(d => d.Symbol.Length)
            .ThenBy(d => d.Symbol, StringComparer.Ordinal)
            .ThenBy(d => d.Path, StringComparer.Ordinal)];
    }

    /// <summary>
    /// What <paramref name="definitions"/> (one symbol) call, construct or inherit, and its members' too when it is a
    /// type, with the files defining those. Only links its readable defining files assert, to definitions the caller
    /// may read.
    /// </summary>
    private static async Task<(List<CodeUse> Uses, List<CodeFileMatch> Files)> UsesAsync(Call call, List<CodeDefinition> definitions, CancellationToken ct)
    {
        string[] documents = [.. definitions.Select(d => d.DocumentId).Distinct(StringComparer.Ordinal)];
        string node = NodeIdOf(call, definitions[0]);
        List<EdgeRow> members = await call.Context.Database.SqlQueryRaw<EdgeRow>(
            """
            SELECT e.from_node AS "FromNode", e.to_node AS "ToNode", e.type AS "Type", e.evidence AS "Evidence",
                   e.confidence AS "Confidence", e.document_id AS "DocumentId", '' AS "Path"
            FROM link_edges e
            WHERE e.from_node = {0} AND e.type = 'defines' AND e.document_id = ANY({1})
            """, node, documents).ToListAsync(ct);
        string[] from = [node, .. members.Select(m => m.ToNode).Distinct(StringComparer.Ordinal)];

        List<EdgeRow> outgoing = await call.Context.Database.SqlQueryRaw<EdgeRow>(
            """
            SELECT e.from_node AS "FromNode", e.to_node AS "ToNode", e.type AS "Type", e.evidence AS "Evidence",
                   e.confidence AS "Confidence", e.document_id AS "DocumentId", '' AS "Path"
            FROM link_edges e
            WHERE e.from_node = ANY({0}) AND e.type IN ('calls', 'inherits') AND e.document_id = ANY({1})
            """, from, documents).ToListAsync(ct);
        string[] targets = [.. outgoing.Select(e => e.ToNode).Where(t => !from.Contains(t)).Distinct(StringComparer.Ordinal)];
        List<CodeDefinition> defined = await DefinitionsAsync(call, [.. targets], ct);

        List<CodeUse> uses = [];
        foreach (EdgeRow edge in outgoing.OrderByDescending(e => e.Confidence).ThenBy(e => LineOf(e.Evidence)))
        {
            foreach (CodeDefinition target in defined.Where(d => d.Symbol == Key(edge.ToNode) && d.Language == Language(edge.ToNode)))
                uses.Add(new CodeUse(target.Symbol, target.Kind, target.Path, target.StartLine, edge.Type, edge.Confidence));
        }
        uses = [.. uses.DistinctBy(u => (u.Symbol, u.Path))];

        List<CodeFileMatch> files = [.. uses
            .Where(u => !documents.Contains(defined.First(d => d.Symbol == u.Symbol && d.Path == u.Path).DocumentId))
            .GroupBy(u => u.Path, StringComparer.Ordinal)
            .Select(g =>
            {
                CodeDefinition d = defined.First(x => x.Path == g.Key);
                return new CodeFileMatch(g.Key, d.DocumentId, 1.0 + g.Max(u => u.Confidence) + Math.Min(0.09, g.Count() * 0.01),
                    "defines what it uses", [.. g.Take(5).Select(u => new CodeLine(u.Line, u.Symbol))]);
            })];
        return (uses, files);
    }

    /// <summary>
    /// Files defining names its body uses that no link resolved: a call on a variable whose type the file does not
    /// say, a type in a declaration. A name defined in few places says more, and one in the definition's own language
    /// and nearer its file is preferred.
    /// </summary>
    private static async Task<List<CodeFileMatch>> BodyNamesAsync(Call call, List<CodeDefinition> definitions, CancellationToken ct)
    {
        CodeDefinition first = definitions[0];
        Dictionary<string, Shape> shapes = await ShapesAsync(call, [first.DocumentId], withText: true, ct);
        (_, string body) = Body(shapes.GetValueOrDefault(first.DocumentId) ?? Shape.Empty, first.Symbol, first.StartLine);
        if (body.Length == 0)
            return [];

        HashSet<string> own = [.. first.Symbol.Split('.', '#', '/')];
        string[] names = [.. IdentifierPattern().Matches(body).Select(m => m.Value)
            .Where(n => n.Length >= 3 && !own.Contains(n) && !CommonWords.Contains(n))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxBodyNames)];
        if (names.Length == 0)
            return [];

        List<EdgeRow> defines = await call.Context.Database.SqlQueryRaw<EdgeRow>(
            """
            SELECT e.from_node AS "FromNode", e.to_node AS "ToNode", e.type AS "Type", e.evidence AS "Evidence",
                   e.confidence AS "Confidence", e.document_id AS "DocumentId", d.path AS "Path"
            FROM link_edges e
            INNER JOIN documents d ON d.id::text = e.document_id
            WHERE d.owner_id = {0} AND e.type = 'defines' AND e.to_node LIKE 'code:%'
              AND e.to_node NOT LIKE '%:csharp-namespace:%'
              AND regexp_replace(e.to_node, '^.*[.#:]', '') = ANY({1})
            """, call.Scope.OwnerId, names).ToListAsync(ct);
        defines = [.. defines.Where(e => Language(e.ToNode) == first.Language)];
        IReadOnlySet<string> readable = await call.ReadableAsync(defines.Select(e => e.DocumentId), ct);

        string directory = Directory(first.Path);
        Dictionary<string, (double Score, string Path, List<CodeLine> Lines)> files = new(StringComparer.Ordinal);
        foreach (IGrouping<string, EdgeRow> name in defines.Where(e => readable.Contains(e.DocumentId))
                     .GroupBy(e => LastSegment(Key(e.ToNode)), StringComparer.Ordinal))
        {
            List<EdgeRow> candidates = [.. name.DistinctBy(e => e.DocumentId)];
            if (candidates.Count > MaxNameCandidates)
                continue;
            foreach (EdgeRow candidate in candidates)
            {
                if (candidate.DocumentId == first.DocumentId)
                    continue;
                double near = Directory(candidate.Path) == directory ? 1.0 : 0.7;
                double score = near / candidates.Count;
                (double s, string p, List<CodeLine> lines) = files.GetValueOrDefault(candidate.DocumentId, (0, candidate.Path, []));
                lines.Add(new CodeLine(LineOf(candidate.Evidence), Key(candidate.ToNode)));
                files[candidate.DocumentId] = (s + score, p, lines);
            }
        }

        return [.. files.Select(f => new CodeFileMatch(f.Value.Path, f.Key, Math.Min(0.99, f.Value.Score / 2),
            "defines a name its body uses", [.. f.Value.Lines.Take(5)]))];
    }

    // ---- references ----------------------------------------------------------------------------------------------

    private static async Task<CodeReferencesResult> ReferencesCoreAsync(Call call, string symbol, CodeReferenceKind kind, int limit, CancellationToken ct)
    {
        List<CodeDefinition> definitions = await DefinitionsAsync(call, await NodesByNameAsync(call, symbol, ct), ct);
        if (definitions.Count == 0)
        {
            // No node, so no links: the files that declare the name, then every file that names it.
            if (kind == CodeReferenceKind.Inherits)
                return new CodeReferencesResult([], []);
            List<CodeFileMatch> declared = await DeclaredInAsync(call, symbol, ct);
            if (declared.Count == 0)
                return new CodeReferencesResult([], []);
            List<CodeFileMatch> mentioned = await NamedInAsync(call, Normalize(symbol), ct);
            return new CodeReferencesResult([], [.. Merge(declared, mentioned).Take(limit)]);
        }

        string[] types = kind switch
        {
            CodeReferenceKind.Calls => [Calls],
            CodeReferenceKind.Inherits => [Inherits],
            _ => [Calls, Inherits, Imports],
        };
        string[] nodes = [.. definitions.Select(d => NodeIdOf(call, d)).Distinct(StringComparer.Ordinal)];
        List<EdgeRow> incoming = await call.Context.Database.SqlQueryRaw<EdgeRow>(
            """
            SELECT e.from_node AS "FromNode", e.to_node AS "ToNode", e.type AS "Type", e.evidence AS "Evidence",
                   e.confidence AS "Confidence", e.document_id AS "DocumentId", d.path AS "Path"
            FROM link_edges e
            INNER JOIN documents d ON d.id::text = e.document_id
            WHERE d.owner_id = {0} AND e.to_node = ANY({1}) AND e.type = ANY({2})
            """, call.Scope.OwnerId, nodes, types).ToListAsync(ct);
        IReadOnlySet<string> readable = await call.ReadableAsync(incoming.Select(e => e.DocumentId), ct);

        Dictionary<string, CodeFileMatch> files = new(StringComparer.Ordinal);
        foreach (IGrouping<string, EdgeRow> file in incoming.Where(e => readable.Contains(e.DocumentId)).GroupBy(e => e.DocumentId, StringComparer.Ordinal))
        {
            double best = file.Max(e => e.Confidence);
            files[file.Key] = new CodeFileMatch(file.First().Path, file.Key, 2.0 + best + Math.Min(0.09, file.Count() * 0.01),
                ReasonFor(file.Select(e => e.Type)), [.. file.OrderBy(e => LineOf(e.Evidence)).Take(5).Select(e => new CodeLine(LineOf(e.Evidence), EvidenceText(e.Evidence)))]);
        }

        if (kind != CodeReferenceKind.Inherits)
        {
            foreach (CodeFileMatch named in await NamedInAsync(call, definitions[0].Symbol, ct))
                files.TryAdd(named.DocumentId, named);
        }

        return new CodeReferencesResult(definitions, [.. files.Values
            .OrderByDescending(f => f.Score).ThenBy(f => f.Path, StringComparer.Ordinal).Take(limit)]);
    }

    /// <summary>
    /// Files whose text names the symbol as a whole word, where no link says they use it: a call through a variable of
    /// unknown type, a declaration, a mention. A file that also names the symbol's type ranks above one that does not.
    /// </summary>
    private static async Task<List<CodeFileMatch>> NamedInAsync(Call call, string symbol, CancellationToken ct)
    {
        string name = LastSegment(symbol);
        string? qualifier = Qualifier(symbol);
        string named = $@"\m{Regex.Escape(name)}\M";
        string qualified = qualifier is null ? named : $@"\m{Regex.Escape(qualifier)}\M";
        List<TextRow> rows = await call.Context.Database.SqlQueryRaw<TextRow>(
            """
            SELECT c.document_id::text AS "DocumentId", d.path AS "Path",
                   sum(regexp_count(c.content, {1}))::int AS "Hits",
                   bool_or(c.content ~ {2}) AS "Qualified"
            FROM chunks c
            INNER JOIN documents d ON d.id = c.document_id
            WHERE c.owner_id = {0} AND c.content ~ {1}
            GROUP BY c.document_id, d.path
            """, call.Scope.OwnerId, named, qualified).ToListAsync(ct);
        IReadOnlySet<string> readable = await call.ReadableAsync(rows.Select(r => r.DocumentId), ct);

        return [.. rows.Where(r => readable.Contains(r.DocumentId)).Select(r =>
            new CodeFileMatch(r.Path, r.DocumentId, (r.Qualified ? 1.0 : 0.5) * r.Hits / (r.Hits + 2.0),
                r.Hits == 1 ? "names it once" : $"names it {r.Hits} times", []))];
    }

    /// <summary>
    /// Files with a line that declares the name — a constant, field, property, type alias or Python attribute — for a
    /// symbol the code index keeps no node for. With <c>Type.member</c>, a file that also declares the type ranks first.
    /// </summary>
    private static async Task<List<CodeFileMatch>> DeclaredInAsync(Call call, string query, CancellationToken ct)
    {
        string symbol = Normalize(query);
        string name = LastSegment(symbol);
        if (!IdentifierOnlyPattern().IsMatch(name))
            return [];
        int dot = symbol.Length - name.Length - 1;
        string? owner = dot > 0 ? LastSegment(symbol[..dot]) : null;
        if (owner is not null && !IdentifierOnlyPattern().IsMatch(owner))
            owner = null;

        string n = Regex.Escape(name);
        // Postgres advanced regex, newline-sensitive: a keyword before the name; the name opening a line, then ':', '='
        // or ';' (Python and TypeScript members, self.x = …); or a typed C# field or property (no call before the name).
        string declares =
            $@"(?n)\m(const|let|var|type|interface|enum|class|struct|record|def|function)[ \t]+{n}\M"
            + $@"|^[ \t]*((public|private|protected|readonly|static|declare|override|abstract|export)[ \t]+)*((self|this|cls)\.)?#?{n}[ \t]*[?!]?[ \t]*(:[^:]|=[^=>]|;)"
            + $@"|^[ \t]*(?!(return|await|throw|yield|case|else|using|var)[ \t])[A-Za-z_][^=;(){{}}]*[ \t]{n}[ \t]*(=[^=]|;|\{{|=>)";
        string ownerDeclared = owner is null ? declares : $@"\m(class|struct|record|interface|enum|type|object)[ \t]+{Regex.Escape(owner)}\M";

        List<TextRow> rows = await call.Context.Database.SqlQueryRaw<TextRow>(
            """
            SELECT c.document_id::text AS "DocumentId", d.path AS "Path",
                   sum(regexp_count(c.content, {1}))::int AS "Hits",
                   bool_or(c.content ~ {2}) AS "Qualified"
            FROM chunks c
            INNER JOIN documents d ON d.id = c.document_id
            WHERE c.owner_id = {0} AND c.content ~ {1}
            GROUP BY c.document_id, d.path
            """, call.Scope.OwnerId, declares, ownerDeclared).ToListAsync(ct);
        IReadOnlySet<string> readable = await call.ReadableAsync(rows.Select(r => r.DocumentId), ct);

        return [.. rows.Where(r => readable.Contains(r.DocumentId)).Select(r =>
            new CodeFileMatch(r.Path, r.DocumentId, (r.Qualified ? 2.0 : 1.0) + Math.Min(0.09, r.Hits * 0.01),
                r.Qualified && owner is not null ? $"declares it in {owner}" : "declares it", []))];
    }

    // ---- impact --------------------------------------------------------------------------------------------------

    private static async Task<CodeImpactResult> ImpactCoreAsync(Call call, string symbol, int depth, int limit, CancellationToken ct)
    {
        List<CodeDefinition> definitions = await DefinitionsAsync(call, await NodesByNameAsync(call, symbol, ct), ct);
        if (definitions.Count == 0)
            return new CodeImpactResult([], [], [], []);

        // A type changes with its members, so what calls a member is affected too.
        string[] roots = [.. definitions.Select(d => NodeIdOf(call, d)).Distinct(StringComparer.Ordinal)];
        List<EdgeRow> members = await call.Context.Database.SqlQueryRaw<EdgeRow>(
            """
            SELECT e.from_node AS "FromNode", e.to_node AS "ToNode", e.type AS "Type", e.evidence AS "Evidence",
                   e.confidence AS "Confidence", e.document_id AS "DocumentId", '' AS "Path"
            FROM link_edges e
            WHERE e.from_node = ANY({0}) AND e.type = 'defines' AND e.document_id = ANY({1})
            """, roots, definitions.Select(d => d.DocumentId).Distinct(StringComparer.Ordinal).ToArray()).ToListAsync(ct);

        Dictionary<string, (int Hops, double Confidence, string Path, string Document)> reached = new(StringComparer.Ordinal);
        HashSet<string> seen = [.. roots, .. members.Select(m => m.ToNode)];
        List<(string Node, double Confidence)> frontier = [.. seen.Select(n => (n, 1.0))];
        for (int hop = 1; hop <= depth && frontier.Count > 0; hop++)
        {
            Dictionary<string, double> carried = frontier.GroupBy(f => f.Node).ToDictionary(g => g.Key, g => g.Max(f => f.Confidence), StringComparer.Ordinal);
            List<EdgeRow> incoming = await call.Context.Database.SqlQueryRaw<EdgeRow>(
                """
                SELECT e.from_node AS "FromNode", e.to_node AS "ToNode", e.type AS "Type", e.evidence AS "Evidence",
                       e.confidence AS "Confidence", e.document_id AS "DocumentId", d.path AS "Path"
                FROM link_edges e
                INNER JOIN documents d ON d.id::text = e.document_id
                WHERE d.owner_id = {0} AND e.to_node = ANY({1}) AND e.type IN ('calls', 'inherits')
                """, call.Scope.OwnerId, carried.Keys.ToArray()).ToListAsync(ct);
            IReadOnlySet<string> readable = await call.ReadableAsync(incoming.Select(e => e.DocumentId), ct);

            List<(string Node, double Confidence)> next = [];
            foreach (EdgeRow edge in incoming.Where(e => readable.Contains(e.DocumentId)))
            {
                double confidence = carried[edge.ToNode] * edge.Confidence;
                if (seen.Contains(edge.FromNode) && !reached.ContainsKey(edge.FromNode))
                    continue;
                if (reached.TryGetValue(edge.FromNode, out var known))
                {
                    if (known.Hops == hop && confidence > known.Confidence)
                        reached[edge.FromNode] = (hop, confidence, edge.Path, edge.DocumentId);
                    continue;
                }
                seen.Add(edge.FromNode);
                reached[edge.FromNode] = (hop, confidence, edge.Path, edge.DocumentId);
                next.Add((edge.FromNode, confidence));
            }
            frontier = next;
        }

        Dictionary<string, string> kinds = await KindsAsync(call, reached.Keys, reached.Values.Select(r => r.Document), ct);
        List<CodeImpacted> symbols = [.. reached
            .Select(r => new CodeImpacted(
                Language(r.Key) == "file" ? r.Value.Path : Key(r.Key),
                kinds.GetValueOrDefault(r.Key, "symbol"),
                r.Value.Path, r.Value.Hops, Math.Round(r.Value.Confidence, 3),
                IsTest(r.Value.Path) || Key(r.Key).Contains("Test", StringComparison.Ordinal)))
            .OrderBy(s => s.Hops).ThenByDescending(s => s.Confidence).ThenBy(s => s.Symbol, StringComparer.Ordinal)];

        List<CodeFileMatch> files = [.. reached
            .GroupBy(r => r.Value.Document, StringComparer.Ordinal)
            .Select(g => new CodeFileMatch(g.First().Value.Path, g.Key,
                g.Max(r => r.Value.Confidence / r.Value.Hops),
                $"reaches it in {g.Min(r => r.Value.Hops)} hop(s)",
                [.. g.OrderBy(r => r.Value.Hops).Take(5).Select(r => new CodeLine(null, Language(r.Key) == "file" ? "(top level)" : Key(r.Key)))]))
            .OrderByDescending(f => f.Score).ThenBy(f => f.Path, StringComparer.Ordinal)
            .Take(limit)];

        return new CodeImpactResult(definitions, [.. symbols.Take(limit)], files,
            [.. files.Where(f => IsTest(f.Path) || symbols.Any(s => s.Path == f.Path && s.IsTest)).Select(f => f.Path)]);
    }

    // ---- repository map ------------------------------------------------------------------------------------------

    private static async Task<CodeRepoMap> RepoMapCoreAsync(Call call, string? pathPrefix, int maxFiles, CancellationToken ct)
    {
        string prefix = string.IsNullOrWhiteSpace(pathPrefix) ? "" : "/" + pathPrefix.Trim().Replace('\\', '/').TrimStart('/');
        List<EdgeRow> defines = await call.Context.Database.SqlQueryRaw<EdgeRow>(
            """
            SELECT e.from_node AS "FromNode", e.to_node AS "ToNode", e.type AS "Type", e.evidence AS "Evidence",
                   e.confidence AS "Confidence", e.document_id AS "DocumentId", d.path AS "Path"
            FROM link_edges e
            INNER JOIN documents d ON d.id::text = e.document_id
            WHERE d.owner_id = {0} AND e.type = 'defines' AND e.to_node LIKE 'code:%'
              AND e.to_node NOT LIKE '%:csharp-namespace:%' AND d.path LIKE {1}
            """, call.Scope.OwnerId, EscapeLike(prefix) + "%").ToListAsync(ct);
        IReadOnlySet<string> readable = await call.ReadableAsync(defines.Select(e => e.DocumentId), ct);
        defines = [.. defines.Where(e => readable.Contains(e.DocumentId))];
        if (defines.Count == 0)
            return new CodeRepoMap([], 0);

        // How many other readable files refer to each definition: the map's rank, as Aider ranks by references.
        string[] nodes = [.. defines.Select(e => e.ToNode).Distinct(StringComparer.Ordinal)];
        List<CountRow> counts = await call.Context.Database.SqlQueryRaw<CountRow>(
            """
            SELECT e.to_node AS "NodeId", e.document_id AS "DocumentId"
            FROM link_edges e
            INNER JOIN documents d ON d.id::text = e.document_id
            WHERE d.owner_id = {0} AND e.to_node = ANY({1}) AND e.type IN ('calls', 'inherits')
            GROUP BY e.to_node, e.document_id
            """, call.Scope.OwnerId, nodes).ToListAsync(ct);
        IReadOnlySet<string> referring = await call.ReadableAsync(counts.Select(c => c.DocumentId), ct);
        Dictionary<string, HashSet<string>> referredBy = counts.Where(c => referring.Contains(c.DocumentId))
            .GroupBy(c => c.NodeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.DocumentId).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

        Dictionary<string, string> kinds = await KindsAsync(call, nodes, defines.Select(e => e.DocumentId), ct);
        List<CodeMapFile> files = [.. defines
            .GroupBy(e => e.DocumentId, StringComparer.Ordinal)
            .Select(file =>
            {
                List<CodeMapSymbol> symbols = [.. file
                    .DistinctBy(e => e.ToNode)
                    .Select(e => new CodeMapSymbol(Key(e.ToNode), kinds.GetValueOrDefault(e.ToNode, "symbol"), LineOf(e.Evidence), null,
                        referredBy.TryGetValue(e.ToNode, out HashSet<string>? by) ? by.Count(d => d != file.Key) : 0))];
                double rank = symbols.Sum(s => Math.Log(1 + s.ReferencedBy));
                return new CodeMapFile(file.First().Path, file.Key, Math.Round(rank, 3), [.. symbols
                    .OrderByDescending(s => IsTypeKind(s.Kind) ? 1 : 0).ThenByDescending(s => s.ReferencedBy).ThenBy(s => s.Line)
                    .Take(12)]);
            })
            .OrderByDescending(f => f.Rank).ThenBy(f => f.Path, StringComparer.Ordinal)];

        return new CodeRepoMap([.. files.Take(maxFiles)], files.Count);
    }

    // ---- shared reads ----------------------------------------------------------------------------------------------

    /// <summary>Each node's kind, as any of <paramref name="documents"/> gave it.</summary>
    private static async Task<Dictionary<string, string>> KindsAsync(Call call, IEnumerable<string> nodes, IEnumerable<string> documents, CancellationToken ct)
    {
        string[] ids = [.. nodes.Distinct(StringComparer.Ordinal)];
        if (ids.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        List<FactRow> facts = await call.Context.Database.SqlQueryRaw<FactRow>(
            """
            SELECT f.node_id AS "NodeId", f.field AS "Field", f.text AS "Text", f.document_id AS "DocumentId"
            FROM link_node_facts f
            WHERE f.node_id = ANY({0}) AND f.field = 0 AND f.document_id = ANY({1})
            """, ids, documents.Distinct(StringComparer.Ordinal).ToArray()).ToListAsync(ct);
        return facts.GroupBy(f => f.NodeId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Text, StringComparer.Ordinal);
    }

    /// <summary>A document's code passages and where each of its definitions starts.</summary>
    internal sealed record Shape(IReadOnlyList<ChunkRow> Chunks, IReadOnlyList<(string Key, int Line)> Starts)
    {
        public static Shape Empty { get; } = new([], []);
    }

    /// <summary>
    /// The code passages of each document, with their lines (and text when <paramref name="withText"/>), and the line
    /// each definition in it starts on.
    /// </summary>
    private static async Task<Dictionary<string, Shape>> ShapesAsync(Call call, IEnumerable<string> documents, bool withText, CancellationToken ct)
    {
        string[] texts = [.. documents.Distinct(StringComparer.Ordinal)];
        Guid[] ids = [.. texts.Select(Guid.Parse)];
        List<ChunkRow> rows = await call.Context.Database.SqlQueryRaw<ChunkRow>(
            """
            SELECT c.document_id::text AS "DocumentId", c.metadata->>'CodeSymbol' AS "Symbol", c.metadata->>'CodeKind' AS "Kind",
                   c.metadata->>'StartLine' AS "StartLine", c.metadata->>'EndLine' AS "EndLine",
                   CASE WHEN {1} THEN c.content ELSE '' END AS "Content"
            FROM chunks c
            WHERE c.document_id = ANY({0}) AND c.metadata->>'StartLine' IS NOT NULL
            """, ids, withText).ToListAsync(ct);
        List<EdgeRow> defines = await call.Context.Database.SqlQueryRaw<EdgeRow>(
            """
            SELECT e.from_node AS "FromNode", e.to_node AS "ToNode", e.type AS "Type", e.evidence AS "Evidence",
                   e.confidence AS "Confidence", e.document_id AS "DocumentId", '' AS "Path"
            FROM link_edges e
            WHERE e.document_id = ANY({0}) AND e.type = 'defines'
            """, [texts]).ToListAsync(ct);

        ILookup<string, ChunkRow> chunks = rows.ToLookup(r => r.DocumentId, StringComparer.Ordinal);
        ILookup<string, EdgeRow> starts = defines.ToLookup(e => e.DocumentId, StringComparer.Ordinal);
        return texts.ToDictionary(d => d, d => new Shape([.. chunks[d]],
            [.. starts[d].Select(e => (Key(e.ToNode), LineOf(e.Evidence)))]), StringComparer.Ordinal);
    }

    /// <summary>
    /// The end line and text of the definition of <paramref name="key"/> starting at <paramref name="line"/>. A
    /// definition with passages of its own is those passages; one read as part of its type's passage ends before the
    /// next definition outside it starts, or where that passage ends.
    /// </summary>
    internal static (int? EndLine, string Body) Body(Shape shape, string key, int line)
    {
        List<(int Start, int End, string Text)> own = [.. shape.Chunks
            .Where(c => c.Symbol == key)
            .Select(c => (Start: ParseInt(c.StartLine), End: ParseInt(c.EndLine), Text: Code(c)))
            .Where(c => c.Start >= line)
            .OrderBy(c => c.Start)];
        if (own.Count > 0)
        {
            List<(int Start, int End, string Text)> run = [own[0]];
            foreach (var part in own.Skip(1))
            {
                if (part.Start > run[^1].End + 1)
                    break;
                run.Add(part);
            }
            return (run[^1].End, string.Join("\n", run.Select(r => r.Text)));
        }

        ChunkRow? around = shape.Chunks
            .Where(c => ParseInt(c.StartLine) <= line && ParseInt(c.EndLine) >= line)
            .OrderBy(c => ParseInt(c.EndLine) - ParseInt(c.StartLine))
            .FirstOrDefault();
        if (around is null)
            return (null, "");
        int start = ParseInt(around.StartLine);
        int end = ParseInt(around.EndLine);
        int next = shape.Starts
            .Where(s => s.Line > line && !IsInside(s.Key, key))
            .Select(s => s.Line)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        end = Math.Min(end, next - 1);

        // A passage cut whole from its type lines up with the file; a member of a merged or elided passage may not.
        string[] lines = Code(around).Split('\n');
        int from = line - start, to = end - start;
        string body = from >= 0 && from < lines.Length
            ? string.Join("\n", lines[from..Math.Min(lines.Length, to + 1)])
            : string.Join("\n", lines);
        return (end, body);
    }

    /// <summary>A passage's code, without the "kind name" line the chunker puts above it.</summary>
    private static string Code(ChunkRow chunk)
    {
        if (chunk.Kind is null or "file" || chunk.Content.Length == 0)
            return chunk.Content;
        int newline = chunk.Content.IndexOf('\n');
        return newline < 0 ? "" : chunk.Content[(newline + 1)..];
    }

    private static bool IsInside(string key, string outer) =>
        key.Length > outer.Length && key.StartsWith(outer, StringComparison.Ordinal) && key[outer.Length] is '.' or '#';

    // ---- names -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The node id of a definition. Nodes are keyed by the owner that ingested them ("c" for a container, "s" for a
    /// source); every definition shown was read from a node in this scope, which recorded that segment.
    /// </summary>
    private static string NodeIdOf(Call call, CodeDefinition definition) =>
        $"code:{call.OwnerSegment ?? throw new InvalidOperationException("A definition read before any node.")}:{definition.Language}:{definition.Symbol}";

    private static string OwnerSegmentOf(string nodeId)
    {
        string[] parts = nodeId.Split(':', 4);
        return parts.Length == 4 ? parts[1] : "";
    }

    /// <summary>The key of a code node id: everything after <c>code:&lt;owner&gt;:&lt;language&gt;:</c>.</summary>
    internal static string Key(string nodeId)
    {
        string[] parts = nodeId.Split(':', 4);
        return parts.Length == 4 ? parts[3] : nodeId;
    }

    internal static string Language(string nodeId)
    {
        string[] parts = nodeId.Split(':', 4);
        return parts.Length == 4 ? parts[2] : "";
    }

    /// <summary>Whether <paramref name="key"/> is <paramref name="name"/> or ends with it after a '.' or '#'.</summary>
    internal static bool KeyEndsWith(string key, string name) =>
        key == name || (key.EndsWith(name, StringComparison.Ordinal) && key[^(name.Length + 1)] is '.' or '#');

    internal static string LastSegment(string key)
    {
        int at = key.LastIndexOfAny(['.', '#', '/']);
        return at < 0 ? key : key[(at + 1)..];
    }

    /// <summary>The type a member belongs to (<c>Ledger</c> for <c>Acme.Billing.Ledger.Post</c>), when its name looks like one.</summary>
    internal static string? Qualifier(string key)
    {
        int at = key.LastIndexOfAny(['.', '#']);
        if (at <= 0 || key[at] == '#')
            return null;
        string parent = LastSegment(key[..at]);
        return parent.Length > 0 && char.IsUpper(parent[0]) ? parent : null;
    }

    /// <summary>A name as an agent might write it: backticks, a trailing <c>()</c> and C++-style <c>::</c> removed.</summary>
    internal static string Normalize(string query)
    {
        string name = query.Trim().Trim('`').Trim();
        if (name.EndsWith("()", StringComparison.Ordinal))
            name = name[..^2];
        return name.Replace("::", ".", StringComparison.Ordinal);
    }

    internal static int LineOf(string evidence)
    {
        Match match = EvidenceLinePattern().Match(evidence);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    internal static string EvidenceText(string evidence)
    {
        int at = evidence.IndexOf(") ", StringComparison.Ordinal);
        return at < 0 ? evidence : evidence[(at + 2)..];
    }

    private static string ReasonFor(IEnumerable<string> types)
    {
        HashSet<string> set = [.. types];
        return set.Contains(Calls) ? "calls it" : set.Contains(Inherits) ? "inherits it" : "imports it";
    }

    internal static bool IsTest(string path) => TestPathPattern().IsMatch(path);

    private static bool IsTypeKind(string kind) => kind is "class" or "struct" or "record" or "interface" or "enum" or "type";

    private static string Directory(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static int ParseInt(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static List<CodeFileMatch> Merge(params List<CodeFileMatch>[] tiers)
    {
        Dictionary<string, CodeFileMatch> files = new(StringComparer.Ordinal);
        foreach (CodeFileMatch file in tiers.SelectMany(t => t))
        {
            if (!files.TryGetValue(file.DocumentId, out CodeFileMatch? known) || known.Score < file.Score)
                files[file.DocumentId] = file;
        }
        return [.. files.Values.OrderByDescending(f => f.Score).ThenBy(f => f.Path, StringComparer.Ordinal)];
    }

    /// <summary>Words too common in code to say which definition a body means.</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "var", "new", "return", "await", "async", "this", "self", "cls", "base", "super", "null", "None", "true", "false",
        "True", "False", "string", "int", "bool", "object", "void", "static", "public", "private", "protected", "internal",
        "readonly", "const", "let", "function", "class", "def", "import", "from", "for", "foreach", "while", "if", "else",
        "elif", "try", "catch", "except", "finally", "throw", "raise", "yield", "lambda", "with", "not", "and", "the",
        "typeof", "nameof", "default", "case", "switch", "break", "continue", "using", "namespace", "out", "ref", "params",
        "value", "get", "set", "init", "Task", "List", "Dictionary", "Exception", "String", "Object", "undefined", "number",
        "boolean", "any", "unknown", "never", "export", "interface", "type", "extends", "implements", "instanceof",
        "ToString", "Equals", "GetHashCode", "Length", "Count", "Add", "Contains",
    };

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"[A-Za-z0-9_]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex IdentifierOnlyPattern();

    [GeneratedRegex(@":(\d+) \(")]
    private static partial Regex EvidenceLinePattern();

    [GeneratedRegex(@"(^|/)(tests?|specs?|__tests__|testing)(/|$)|([Tt]ests?|[Ss]pec)\.[A-Za-z]+$|(^|/)test_[^/]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TestPathPattern();

    // ---- rows ------------------------------------------------------------------------------------------------------

    private sealed class NodeRow
    {
        public string NodeId { get; init; } = "";
    }

    private sealed class EdgeRow
    {
        public string FromNode { get; init; } = "";
        public string ToNode { get; init; } = "";
        public string Type { get; init; } = "";
        public string Evidence { get; init; } = "";
        public float Confidence { get; init; }
        public string DocumentId { get; init; } = "";
        public string Path { get; init; } = "";
    }

    private sealed class FactRow
    {
        public string NodeId { get; init; } = "";
        public short Field { get; init; }
        public string Text { get; init; } = "";
        public string DocumentId { get; init; } = "";
    }

    internal sealed class ChunkRow
    {
        public string DocumentId { get; init; } = "";
        public string? Symbol { get; init; }
        public string? Kind { get; init; }
        public string? StartLine { get; init; }
        public string? EndLine { get; init; }
        public string Content { get; init; } = "";
    }

    private sealed class TextRow
    {
        public string DocumentId { get; init; } = "";
        public string Path { get; init; } = "";
        public int Hits { get; init; }
        public bool Qualified { get; init; }
    }

    private sealed class CountRow
    {
        public string NodeId { get; init; } = "";
        public string DocumentId { get; init; } = "";
    }
}

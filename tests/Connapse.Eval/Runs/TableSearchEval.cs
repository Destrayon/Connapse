using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Connapse.Application.Tables;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Statistics;
using Connapse.Eval.Systems;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Parquet.Serialization;

namespace Connapse.Eval.Runs;

/// <summary>
/// The table-search proof (CON-310, bar in <c>eval/pass-bars.md</c>, "Finding tables by description"): Spider's 876
/// tables loaded as one container's tables, named "&lt;database&gt; &lt;table&gt;" with their original headers and no
/// descriptions, and each of Spider's 1,034 dev questions used as the search phrase. A question is found when every
/// table its gold SQL reads after FROM or JOIN is in the first 10 ranked. The keyword arm ranks by the cards' words
/// alone; the hybrid arm fuses that with the cards' embeddings, through the same <see cref="ITableCardIndex"/> and
/// <see cref="TableSearch"/> that <c>table_list</c> uses.
/// </summary>
public sealed partial class TableSearchEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    /// <summary>The manifest dataset whose pinned files this reads; table-search-v1 is its only suite.</summary>
    public const string Dataset = "spider-tables";

    private const int TopK = 10;

    public sealed class SpiderQuestionRow
    {
        [JsonPropertyName("db_id")] public string DbId { get; set; } = "";
        [JsonPropertyName("query")] public string Query { get; set; } = "";
        [JsonPropertyName("question")] public string Question { get; set; } = "";
    }

    public sealed record Question(int Index, string DbId, string Text, string Sql, string[] Gold, string[] Unresolved);

    public async Task<int> RunAsync(string configName, string split, string? outPath, CancellationToken ct)
    {
        if (split is not ("tune" or "test"))
            throw new ArgumentException("--split must be tune or test.");
        DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[Dataset];
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        await cache.EnsureAsync(Dataset, entry, allowUnpinned: false, ct);
        string root = cache.DirectoryFor(Dataset, entry);

        Dictionary<string, Dictionary<string, (string Name, string[] Columns)>> databases =
            ReadTables(Path.Combine(root, "tables.json"));
        IList<SpiderQuestionRow> rows = (await ParquetSerializer.DeserializeAsync<SpiderQuestionRow>(
            Path.Combine(root, "dev.parquet"), cancellationToken: ct)).Data;
        List<Question> all = [.. rows.Select((r, i) => ToQuestion(i, r, databases))];
        List<Question> questions = [.. all.Where(q => SplitOf(q.DbId) == split)];
        log.WriteLine($"{databases.Values.Sum(d => d.Count)} tables in {databases.Count} databases; "
            + $"{all.Count} dev questions, {questions.Count} in {split}");

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", configName);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(
            config, paths.WebContentRoot, embeddings, log, embeddingOverride: null, ct);
        await using AsyncServiceScope scope = system.Services.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;

        var container = await services.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest("spider-tables", null, ContainerKind.Documents), ct);
        Guid containerId = Guid.Parse(container.Id);
        ITableStore store = services.GetRequiredService<ITableStore>();
        Dictionary<string, Guid> idOf = new(StringComparer.Ordinal);
        List<TableInfo> tables = [];
        JsonArray loadFailures = [];
        foreach ((string db, Dictionary<string, (string Name, string[] Columns)> dbTables) in databases.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            foreach ((string Name, string[] Columns) table in dbTables.Values)
            {
                string name = $"{db} {table.Name}";
                try
                {
                    TableInfo created = await store.CreateAsync(containerId, new TableLoad(name,
                        [.. table.Columns.Select(c => new TableColumn(c, TableColumnType.Text))], NoRows()), ct);
                    tables.Add(created);
                    idOf[Key(db, table.Name)] = created.Id;
                }
                catch (ArgumentException ex)
                {
                    // Kept and flagged, never dropped silently: a question that needs this table can't be found.
                    loadFailures.Add(new JsonObject { ["table"] = name, ["error"] = ex.Message });
                }
            }
        }

        // As table_list ranks them: sorted by name, so ties keep name order.
        TableCard[] cards = [.. tables.OrderBy(t => t.Name, StringComparer.Ordinal).Select(TableCard.For)];
        ITableCardIndex index = services.GetRequiredService<ITableCardIndex>();
        IEmbeddingProvider embedder = services.GetRequiredService<IEmbeddingProvider>();
        var indexTimer = Stopwatch.StartNew();
        await index.IndexAsync(containerId, cards, ct);
        double indexSeconds = indexTimer.Elapsed.TotalSeconds;
        log.WriteLine($"{cards.Length} cards indexed in {indexSeconds:F1}s, {loadFailures.Count} tables failed to load");

        List<bool> keywordFound = [], hybridFound = [];
        List<double> rankMs = [], queryEmbedMs = [], scoreMs = [], cachedEmbedMs = [], controlMs = [];
        IDbContextFactory<KnowledgeDbContext> dbFactory = services.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        int unembeddedMax = 0;
        bool meaningEverDown = false;
        JsonArray perQuestion = [];
        foreach (Question q in questions)
        {
            Guid[] gold = [.. q.Gold.Select(t => idOf.GetValueOrDefault(Key(q.DbId, t)))];
            IReadOnlyList<Guid> keyword = TableSearch.Rank(cards, q.Text);

            // The query vector is embedded (or read from the disk cache) first and timed alone, so the ranking time
            // below excludes it, as bar 3 says: the index's own embed of the same text is then a cache hit.
            var embedTimer = Stopwatch.StartNew();
            await embedder.EmbedAsync(q.Text, EmbeddingInputType.Query, ct);
            queryEmbedMs.Add(embedTimer.Elapsed.TotalMilliseconds);
            // A second read of the same vector costs what the index's own read will: the harness's disk cache, not
            // the product. It is subtracted from the ranking time.
            var cachedTimer = Stopwatch.StartNew();
            await embedder.EmbedAsync(q.Text, EmbeddingInputType.Query, ct);
            double cached = cachedTimer.Elapsed.TotalMilliseconds;
            cachedEmbedMs.Add(cached);

            // Control: a bare round trip to the same database, to tell the product's time from the host's transport.
            var controlTimer = Stopwatch.StartNew();
            await using (KnowledgeDbContext db = await dbFactory.CreateDbContextAsync(ct))
                await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            controlMs.Add(controlTimer.Elapsed.TotalMilliseconds);

            var rankTimer = Stopwatch.StartNew();
            TableCardSimilarities found = await index.SimilaritiesAsync(containerId, cards, q.Text, ct);
            scoreMs.Add(rankTimer.Elapsed.TotalMilliseconds - cached);
            IReadOnlyList<Guid> hybrid = TableSearch.Rank(cards, q.Text, found.Scores);
            rankMs.Add(rankTimer.Elapsed.TotalMilliseconds - cached);
            unembeddedMax = Math.Max(unembeddedMax, found.Unembedded);
            meaningEverDown |= !found.Available;

            bool resolved = gold.Length > 0 && gold.All(g => g != Guid.Empty);
            bool k = resolved && gold.All(keyword.Take(TopK).Contains);
            bool h = resolved && gold.All(hybrid.Take(TopK).Contains);
            keywordFound.Add(k);
            hybridFound.Add(h);
            perQuestion.Add(new JsonObject
            {
                ["index"] = q.Index,
                ["db"] = q.DbId,
                ["question"] = q.Text,
                ["gold"] = new JsonArray([.. q.Gold.Select(g => (JsonNode)g)]),
                ["unresolved"] = q.Unresolved.Length > 0 ? new JsonArray([.. q.Unresolved.Select(g => (JsonNode)g)]) : null,
                ["keyword"] = k,
                ["hybrid"] = h,
                ["keywordRank"] = new JsonArray([.. gold.Select(g => (JsonNode?)RankOf(keyword, g))]),
                ["hybridRank"] = new JsonArray([.. gold.Select(g => (JsonNode?)RankOf(hybrid, g))]),
            });
        }

        int n = questions.Count;
        int onlyHybrid = Enumerable.Range(0, n).Count(i => hybridFound[i] && !keywordFound[i]);
        int onlyKeyword = Enumerable.Range(0, n).Count(i => keywordFound[i] && !hybridFound[i]);
        double keywordRate = (double)keywordFound.Count(f => f) / n;
        double hybridRate = (double)hybridFound.Count(f => f) / n;
        double p = PairedTests.McNemarExact(onlyHybrid, onlyKeyword);
        double rankP95 = Percentile(rankMs, 0.95);
        int unresolvedQuestions = questions.Count(q => q.Unresolved.Length > 0 || q.Gold.Length == 0);

        bool bar1 = hybridRate >= 0.75;
        bool bar2 = hybridRate - keywordRate >= 0.05 && p < 0.01;
        bool bar3 = rankP95 < 50 && unembeddedMax == 0 && !meaningEverDown;
        var result = new JsonObject
        {
            ["command"] = "table-search",
            ["config"] = configName,
            ["split"] = split,
            ["tables"] = cards.Length,
            ["loadFailures"] = loadFailures,
            ["questions"] = n,
            ["unresolvedGoldQuestions"] = unresolvedQuestions,
            ["found@10"] = new JsonObject { ["keyword"] = Round(keywordRate), ["hybrid"] = Round(hybridRate) },
            ["discordant"] = new JsonObject { ["onlyHybrid"] = onlyHybrid, ["onlyKeyword"] = onlyKeyword },
            ["mcnemarExactP"] = p,
            ["cost"] = new JsonObject
            {
                ["llmTokens"] = 0,
                ["queryEmbeddingsPerSearch"] = 1,
                ["cardIndexSeconds"] = Round(indexSeconds),
                ["maxUnembeddedAtSearch"] = unembeddedMax,
                ["meaningEverUnavailable"] = meaningEverDown,
                ["rankMsP50"] = Round(Percentile(rankMs, 0.5)),
                ["rankMsP95"] = Round(rankP95),
                ["rankMsP95FirstHalf"] = Round(Percentile(rankMs[..(rankMs.Count / 2)], 0.95)),
                ["rankMsP95SecondHalf"] = Round(Percentile(rankMs[(rankMs.Count / 2)..], 0.95)),
                ["scoreInDatabaseMsP50"] = Round(Percentile(scoreMs, 0.5)),
                ["scoreInDatabaseMsP95"] = Round(Percentile(scoreMs, 0.95)),
                ["cachedQueryVectorMsP95"] = Round(Percentile(cachedEmbedMs, 0.95)),
                ["controlSelect1MsP50"] = Round(Percentile(controlMs, 0.5)),
                ["controlSelect1MsP95"] = Round(Percentile(controlMs, 0.95)),
                ["controlSelect1Over30ms"] = controlMs.Count(m => m > 30),
                ["rankOver30ms"] = rankMs.Count(m => m > 30),
                ["queryEmbedMsP50"] = Round(Percentile(queryEmbedMs, 0.5)),
            },
            // The bar is judged on the held-out test split only; tune is reported the same way for reading.
            ["gate"] = new JsonObject
            {
                ["scoredOn"] = split,
                ["bar1HybridAtLeast75"] = bar1,
                ["bar2HybridBeatsKeywordBy5AtP01"] = bar2,
                ["bar3RankP95Under50ms"] = bar3,
                ["keywordMeetsBar1"] = keywordRate >= 0.75,
                ["passed"] = bar1 && bar2 && bar3 && loadFailures.Count == 0,
            },
            ["perQuestion"] = perQuestion,
        };
        string json = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (outPath is not null)
            await File.WriteAllTextAsync(outPath, json + "\n", ct);
        JsonObject summary = result.DeepClone().AsObject();
        summary.Remove("perQuestion");
        log.WriteLine(summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return loadFailures.Count > 0 ? 1 : 0;
    }

    /// <summary>A dev database is tune when the first byte of the SHA-256 of its name is divisible by 4.</summary>
    public static string SplitOf(string dbId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(dbId))[0] % 4 == 0 ? "tune" : "test";

    /// <summary>
    /// The question with its gold tables: every table its SQL reads after FROM or JOIN, matched to its database's
    /// tables ignoring case. A name that matches none is kept in <see cref="Question.Unresolved"/>, and the question
    /// can't be found, rather than dropped.
    /// </summary>
    public static Question ToQuestion(int index, SpiderQuestionRow row,
        IReadOnlyDictionary<string, Dictionary<string, (string Name, string[] Columns)>> databases)
    {
        Dictionary<string, (string Name, string[] Columns)> tables = databases.GetValueOrDefault(row.DbId) ?? [];
        List<string> gold = [], unresolved = [];
        foreach (Match m in FromOrJoin().Matches(row.Query))
        {
            string name = m.Groups[1].Value;
            if (tables.TryGetValue(name, out (string Name, string[] Columns) table))
            {
                if (!gold.Contains(table.Name))
                    gold.Add(table.Name);
            }
            else if (!unresolved.Contains(name, StringComparer.OrdinalIgnoreCase))
                unresolved.Add(name);
        }
        return new Question(index, row.DbId, row.Question, row.Query, [.. gold], [.. unresolved]);
    }

    /// <summary>Each database's tables by original name (ignoring case), with their original column headers.</summary>
    public static Dictionary<string, Dictionary<string, (string Name, string[] Columns)>> ReadTables(string path)
    {
        Dictionary<string, Dictionary<string, (string Name, string[] Columns)>> databases = new(StringComparer.Ordinal);
        foreach (JsonNode? db in JsonNode.Parse(File.ReadAllText(path))!.AsArray())
        {
            string id = db!["db_id"]!.GetValue<string>();
            string[] names = [.. db["table_names_original"]!.AsArray().Select(t => t!.GetValue<string>())];
            List<(int Table, string Column)> columns = [.. db["column_names_original"]!.AsArray()
                .Select(c => (c![0]!.GetValue<int>(), c[1]!.GetValue<string>()))
                .Where(c => c.Item1 >= 0)];
            Dictionary<string, (string Name, string[] Columns)> tables = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Length; i++)
                tables[names[i]] = (names[i], [.. columns.Where(c => c.Table == i).Select(c => c.Column)]);
            databases[id] = tables;
        }
        return databases;
    }

    private static string Key(string db, string table) => $"{db}\u0001{table.ToLowerInvariant()}";

    private static int? RankOf(IReadOnlyList<Guid> ranked, Guid id)
    {
        if (id == Guid.Empty)
            return null;
        for (int i = 0; i < ranked.Count; i++)
            if (ranked[i] == id)
                return i + 1;
        return null;
    }

    private static double Percentile(List<double> values, double q)
    {
        if (values.Count == 0)
            return double.NaN;
        double[] sorted = [.. values.Order()];
        return sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private static double Round(double value) => Math.Round(value, 4);

    private static async IAsyncEnumerable<IReadOnlyList<object?>> NoRows()
    {
        await Task.Yield();
        yield break;
    }

    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex FromOrJoin();
}

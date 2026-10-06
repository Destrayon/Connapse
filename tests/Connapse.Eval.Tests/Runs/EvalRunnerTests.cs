using System.Net;
using System.Security.Cryptography;
using System.Text;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Runs;
using Connapse.Eval.Systems;
using FluentAssertions;
using Parquet.Serialization;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class EvalRunnerTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "eval-repo-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> _extraFiles = new(StringComparer.Ordinal);
    private static readonly byte[] Corpus = Encoding.UTF8.GetBytes("{\"_id\":\"d1\",\"text\":\"one\"}\n{\"_id\":\"d2\",\"text\":\"two\"}\n");
    private static readonly byte[] Queries = Encoding.UTF8.GetBytes("{\"_id\":\"q1\",\"text\":\"one?\"}\n{\"_id\":\"q2\",\"text\":\"two?\"}\n");
    private static readonly byte[] QrelsFile = Encoding.UTF8.GetBytes("query-id\tcorpus-id\tscore\nq1\td1\t1\nq2\td2\t1\n");

    public EvalRunnerTests()
    {
        RepoPaths paths = new(_repo);
        Directory.CreateDirectory(Path.Combine(paths.EvalRoot, "systems", "fake"));
        File.WriteAllText(Path.Combine(paths.EvalRoot, "systems", "fake", "default.json"), "{\"searchMode\":\"hybrid\"}");
        // a and b differ only in search-time settings; c changes the embedding model.
        File.WriteAllText(Path.Combine(paths.EvalRoot, "systems", "fake", "a.json"),
            "{\"searchMode\":\"hybrid\",\"settings\":{\"Knowledge:Search:FusionAlpha\":\"0.5\"}}");
        File.WriteAllText(Path.Combine(paths.EvalRoot, "systems", "fake", "b.json"),
            "{\"searchMode\":\"keyword\",\"settings\":{\"Knowledge:Search:Reranker\":\"CrossEncoder\"}}");
        File.WriteAllText(Path.Combine(paths.EvalRoot, "systems", "fake", "c.json"),
            "{\"searchMode\":\"hybrid\",\"settings\":{\"Knowledge:Embedding:Model\":\"other\"}}");
        DatasetEntry entry = new("beir-jsonl", "1", ["domain:test"],
        [
            new DatasetFile("corpus.jsonl", "https://x.test/c", Hash(Corpus)),
            new DatasetFile("queries.jsonl", "https://x.test/q", Hash(Queries)),
            new DatasetFile("qrels.tsv", "https://x.test/r", Hash(QrelsFile)),
        ]);
        new EvalManifest(
            new Dictionary<string, IReadOnlyList<string>> { ["s"] = ["one", "two"] },
            new Dictionary<string, DatasetEntry> { ["one"] = entry, ["two"] = entry }).Save(paths.ManifestPath);
    }

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private EvalRunner Runner(FakeSystem system) =>
        new(new RepoPaths(_repo), TextWriter.Null, new HttpClient(new FileHandler(_extraFiles)), (_, _) => Task.FromResult<ISystemUnderTest>(system));

    [Fact]
    public async Task RunAsync_ExtractionDataset_RefusesAndPointsToExtract()
    {
        RepoPaths paths = new(_repo);
        new EvalManifest(
            new Dictionary<string, IReadOnlyList<string>> { ["x"] = ["enc"] },
            new Dictionary<string, DatasetEntry> { ["enc"] = new("generated-encodings", "gen-1", [], []) }).Save(paths.ManifestPath);

        Func<Task> act = () => Runner(new FakeSystem(failPerDataset: 0)).RunAsync(new RunRequest("x", "fake", "default", [], null, null), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*'extract' command*");
    }

    [Fact]
    public async Task RunAsync_Suite_WritesEveryDatasetAndScoresIt()
    {
        FakeSystem system = new(failPerDataset: 0);

        RunFolder run = await Runner(system).RunAsync(new RunRequest("s", "fake", "default", [], null, null), CancellationToken.None);

        RunScores scores = Scoring.Score(run);
        scores.Datasets.Should().HaveCount(2).And.OnlyContain(d => !d.Invalid);
        scores.Portfolio["MRR@10"].Should().Be(1.0);
        run.ReadManifest().FinishedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_Resume_SkipsCompletedDatasets()
    {
        FakeSystem first = new(failPerDataset: 0);
        RunFolder run = await Runner(first).RunAsync(new RunRequest("s", "fake", "default", ["one"], null, null), CancellationToken.None);
        FakeSystem second = new(failPerDataset: 0);

        await Runner(second).RunAsync(new RunRequest("s", "fake", "default", [], run.Path, null), CancellationToken.None);

        second.Indexed.Should().Equal("two");
        run.ReadManifest().Datasets.Select(d => d.Name).Should().BeEquivalentTo(["one", "two"]);
    }

    [Fact]
    public async Task RunAsync_MoreThanOnePercentFailed_MarksDatasetInvalid()
    {
        RunFolder run = await Runner(new FakeSystem(failPerDataset: 1))
            .RunAsync(new RunRequest("s", "fake", "default", ["one"], null, null), CancellationToken.None);

        run.ReadManifest().Datasets.Single().Invalid.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ResumeWithDifferentLimitQueries_Refuses()
    {
        RunFolder run = await Runner(new FakeSystem(failPerDataset: 0))
            .RunAsync(new RunRequest("s", "fake", "default", ["one"], null, 1), CancellationToken.None);
        FakeSystem second = new(failPerDataset: 0);

        Func<Task> act = () => Runner(second).RunAsync(new RunRequest("s", "fake", "default", [], run.Path, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1*none*");
        second.Indexed.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ResumeWithChangedDatasetVersion_ThrowsNamingDatasetBeforeStartingSystem()
    {
        RunFolder run = await Runner(new FakeSystem(failPerDataset: 0))
            .RunAsync(new RunRequest("s", "fake", "default", ["one"], null, null), CancellationToken.None);

        RepoPaths paths = new(_repo);
        EvalManifest manifest = EvalManifest.Load(paths.ManifestPath);
        Dictionary<string, DatasetEntry> datasets = new(manifest.Datasets)
        {
            ["one"] = manifest.Datasets["one"] with { Version = "2" },
        };
        manifest = manifest with { Datasets = datasets };
        manifest.Save(paths.ManifestPath);

        bool factoryInvoked = false;
        EvalRunner runner = new(paths, TextWriter.Null, new HttpClient(new FileHandler(_extraFiles)), (_, _) =>
        {
            factoryInvoked = true;
            return Task.FromResult<ISystemUnderTest>(new FakeSystem(failPerDataset: 0));
        });

        Func<Task> act = () => runner.RunAsync(new RunRequest("s", "fake", "default", [], run.Path, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*one*");
        factoryInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_ResumeUnderDifferentSystemDescription_RefusesBeforeIndexing()
    {
        RunFolder run = await Runner(new FakeSystem(failPerDataset: 0))
            .RunAsync(new RunRequest("s", "fake", "default", ["one"], null, null), CancellationToken.None);
        FakeSystem second = new(failPerDataset: 0, kind: "other-model");

        Func<Task> act = () => Runner(second).RunAsync(new RunRequest("s", "fake", "default", [], run.Path, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*kind=fake*kind=other-model*");
        second.Indexed.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_Create_StoresLimitQueriesAndNoResumes()
    {
        RunFolder run = await Runner(new FakeSystem(failPerDataset: 0))
            .RunAsync(new RunRequest("s", "fake", "default", ["one"], null, 3), CancellationToken.None);

        RunManifest manifest = run.ReadManifest();
        manifest.LimitQueries.Should().Be(3);
        manifest.Resumes.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_LimitQueriesWithDevListedBeforeTest_LimitsEachSplitSeparately()
    {
        // RAGBench lists validation (Dev) questions before test (Test) questions.
        RagBenchRow[] train = [new() { Id = "t1", Question = "train q", Documents = ["doc T"], AllRelevantSentenceKeys = ["0a"] }];
        RagBenchRow[] dev =
        [
            new() { Id = "v1", Question = "dev q1", Documents = ["doc A"], AllRelevantSentenceKeys = ["0a"] },
            new() { Id = "v2", Question = "dev q2", Documents = ["doc B"], AllRelevantSentenceKeys = ["0a"] },
        ];
        RagBenchRow[] test =
        [
            new() { Id = "s1", Question = "test q1", Documents = ["doc A"], AllRelevantSentenceKeys = ["0a"] },
            new() { Id = "s2", Question = "test q2", Documents = ["doc B"], AllRelevantSentenceKeys = ["0a"] },
        ];
        List<DatasetFile> files = [];
        foreach ((string name, RagBenchRow[] rows) in new[] { ("train.parquet", train), ("validation.parquet", dev), ("test.parquet", test) })
        {
            using MemoryStream stream = new();
            await ParquetSerializer.SerializeAsync(rows, stream);
            _extraFiles["/" + name] = stream.ToArray();
            files.Add(new DatasetFile(name, "https://x.test/" + name, Hash(stream.ToArray())));
        }
        RepoPaths paths = new(_repo);
        EvalManifest manifest = EvalManifest.Load(paths.ManifestPath);
        new EvalManifest(
            new Dictionary<string, IReadOnlyList<string>>(manifest.Suites) { ["rb"] = ["rb-test"] },
            new Dictionary<string, DatasetEntry>(manifest.Datasets) { ["rb-test"] = new("ragbench", "1", ["domain:test"], files) })
            .Save(paths.ManifestPath);
        FakeSystem system = new(failPerDataset: 0);

        await Runner(system).RunAsync(new RunRequest("rb", "fake", "default", [], null, 1), CancellationToken.None);

        system.Searched.Should().Equal("v1", "s1");
    }

    [Fact]
    public async Task RunAsync_PassageDataset_JudgesRetrievedChunksAndWritesThemAsQrels()
    {
        RepoPaths paths = new(_repo);
        byte[] questions = Encoding.UTF8.GetBytes(
            """{"id":"q1","question":"one?","doc":"a.pdf","evidence":["answer one"],"answer":"one","kind":"table-ruled"}""" + "\n"
            + """{"id":"q2","question":"two?","doc":"a.pdf","evidence":["answer two"],"answer":"two","kind":"columns"}""" + "\n");
        Directory.CreateDirectory(Path.Combine(paths.DatasetsRoot, "pq"));
        File.WriteAllBytes(Path.Combine(paths.DatasetsRoot, "pq", "questions.jsonl"), questions);
        byte[] pdf = Encoding.UTF8.GetBytes("%PDF-");
        _extraFiles["/a.pdf"] = pdf;
        new EvalManifest(
            new Dictionary<string, IReadOnlyList<string>> { ["p"] = ["pq"] },
            new Dictionary<string, DatasetEntry>
            {
                ["pq"] = new("pdf-qa", "1", [],
                [
                    new DatasetFile("a.pdf", "https://x.test/a.pdf", Hash(pdf)),
                    new DatasetFile("questions.jsonl", "repo:pq/questions.jsonl", Hash(questions)),
                ]),
            }).Save(paths.ManifestPath);
        FakeSystem system = new(failPerDataset: 0)
        {
            // q1's answer is the second chunk; q2's answer is never retrieved.
            Passages = [new RetrievedPassage("a.pdf", "c1", "nothing"), new RetrievedPassage("a.pdf", "c2", "Answer: one.")],
        };

        RunFolder run = await Runner(system).RunAsync(new RunRequest("p", "fake", "default", [], null, null), CancellationToken.None);

        RunScores scores = Scoring.Score(run);
        scores.Datasets.Single().PerQuery["q1"]["MRR@10"].Should().Be(0.5);
        scores.Datasets.Single().PerQuery["q2"]["Recall@10"].Should().Be(0);
        run.ReadQrels("pq").For("q1").Should().BeEquivalentTo(new Dictionary<string, int> { ["a.pdf#c1"] = 0, ["a.pdf#c2"] = 1 });
        run.ReadTitles("pq").Should().ContainKey("a.pdf#c2").WhoseValue.Should().Be("a");
        run.ReadResults("pq")[0].Passages.Should().HaveCount(2);
        scores.Datasets.Single().Kinds.Should().HaveCount(2).And.ContainSingle(k =>
            k.Kind == "kind:table-ruled" && k.Queries == 1 && k.Means["MRR@10"] == 0.5);
    }

    // #667: index once per group of configs that share index-time settings, search under each.
    [Fact]
    public async Task RunManyAsync_ConfigsSharingAnIndex_IndexOnceAndWriteARunPerConfig()
    {
        FakeSystem system = new(failPerDataset: 0);
        List<SystemConfig> started = [];
        EvalRunner runner = new(new RepoPaths(_repo), TextWriter.Null, new HttpClient(new FileHandler(_extraFiles)), (start, _) =>
        {
            started.Add(start.Config);
            return Task.FromResult<ISystemUnderTest>(system);
        });

        IReadOnlyList<RunFolder> runs = await runner.RunManyAsync(
            new MultiRunRequest("s", "fake", ["a", "b"], [], null), CancellationToken.None);

        started.Should().ContainSingle().Which.Settings.Should().BeEmpty("the system starts with index-time settings only");
        system.Indexed.Should().Equal("one", "two");
        system.Used.Should().Equal("a", "b", "a", "b");
        system.Searched.Should().HaveCount(8, "2 datasets x 2 queries x 2 configs");
        runs.Select(r => r.ReadManifest().Config).Should().Equal("a", "b");
        runs.Should().OnlyContain(r => r.ReadManifest().FinishedUtc != null && Scoring.Score(r).Portfolio["MRR@10"] == 1.0);
        runs.Select(r => r.ReadManifest().ConfigHash).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task RunManyAsync_IndexTimeSettingDiffers_StartsASystemPerGroup()
    {
        List<FakeSystem> systems = [];
        EvalRunner runner = new(new RepoPaths(_repo), TextWriter.Null, new HttpClient(new FileHandler(_extraFiles)), (_, _) =>
        {
            FakeSystem system = new(failPerDataset: 0);
            systems.Add(system);
            return Task.FromResult<ISystemUnderTest>(system);
        });

        IReadOnlyList<RunFolder> runs = await runner.RunManyAsync(
            new MultiRunRequest("s", "fake", ["a", "c", "b"], ["one"], null), CancellationToken.None);

        systems.Should().HaveCount(2);
        systems[0].Used.Should().Equal("a", "b");
        systems[1].Used.Should().Equal("c");
        systems.Should().OnlyContain(s => s.Indexed.SequenceEqual(new[] { "one" }));
        runs.Select(r => r.ReadManifest().Config).Should().Equal("a", "c", "b");
    }

    [Fact]
    public async Task RunManyAsync_ConfigListedTwice_Refuses()
    {
        Func<Task> act = () => Runner(new FakeSystem(failPerDataset: 0))
            .RunManyAsync(new MultiRunRequest("s", "fake", ["a", "a"], [], null), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*a*");
    }

    // #672: a run that indexes a whole suite saves its index; a later one with the same key restores it.
    private EvalRunner CachingRunner(FakeSystem system, List<SystemStart> starts) =>
        new(new RepoPaths(_repo), TextWriter.Null, new HttpClient(new FileHandler(_extraFiles)), (start, _) =>
        {
            starts.Add(start);
            return Task.FromResult<ISystemUnderTest>(system);
        })
        { UseIndexCache = true };

    [Fact]
    public async Task RunAsync_IndexCacheOn_SavesTheIndexOfEveryDataset()
    {
        FakeSystem system = new(failPerDataset: 0);
        List<SystemStart> starts = [];

        RunFolder run = await CachingRunner(system, starts).RunAsync(new RunRequest("s", "fake", "default", [], null, null), CancellationToken.None);

        starts.Single().IndexCacheKey.Should().NotBeNull();
        system.Saves.Should().ContainSingle().Which.Should().Equal("one", "two");
        run.ReadManifest().IndexCache.Should().Be("saved " + starts.Single().IndexCacheKey);
    }

    [Fact]
    public async Task RunAsync_IndexCacheRestored_SearchesWithoutIndexing()
    {
        FakeSystem system = new(failPerDataset: 0)
        {
            Cached = new Dictionary<string, IndexReport> { ["one"] = new(2, 0, []), ["two"] = new(2, 0, []) },
        };
        List<SystemStart> starts = [];

        RunFolder run = await CachingRunner(system, starts).RunAsync(new RunRequest("s", "fake", "default", [], null, null), CancellationToken.None);

        system.Indexed.Should().BeEmpty();
        system.Saves.Should().BeEmpty();
        Scoring.Score(run).Portfolio["MRR@10"].Should().Be(1.0);
        run.ReadManifest().IndexCache.Should().Be("restored " + starts.Single().IndexCacheKey);
    }

    [Fact]
    public async Task RunAsync_ResumeIndexingPartOfTheSuite_DoesNotUseTheCache()
    {
        RunFolder run = await Runner(new FakeSystem(failPerDataset: 0))
            .RunAsync(new RunRequest("s", "fake", "default", ["one"], null, null), CancellationToken.None);
        FakeSystem system = new(failPerDataset: 0);
        List<SystemStart> starts = [];

        await CachingRunner(system, starts).RunAsync(new RunRequest("s", "fake", "default", [], run.Path, null), CancellationToken.None);

        starts.Single().IndexCacheKey.Should().BeNull();
        system.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task RunManyAsync_IndexCacheOn_KeysOnTheSharedIndexConfig()
    {
        FakeSystem system = new(failPerDataset: 0);
        List<SystemStart> starts = [];

        IReadOnlyList<RunFolder> runs = await CachingRunner(system, starts).RunManyAsync(
            new MultiRunRequest("s", "fake", ["a", "b"], [], null), CancellationToken.None);

        string key = starts.Single().IndexCacheKey!;
        system.Saves.Should().ContainSingle();
        runs.Should().OnlyContain(r => r.ReadManifest().IndexCache == "saved " + key);
        (await IndexCacheKeyForAsync("default")).Should().Be(key, "a and b share default's index-time settings");
    }

    private async Task<string> IndexCacheKeyForAsync(string config)
    {
        List<SystemStart> starts = [];
        await CachingRunner(new FakeSystem(failPerDataset: 0), starts)
            .RunAsync(new RunRequest("s", "fake", config, [], null, null), CancellationToken.None);
        return starts.Single().IndexCacheKey!;
    }

    [Fact]
    public async Task RunAsync_LimitQueriesZero_ThrowsArgumentException()
    {
        Func<Task> act = () => Runner(new FakeSystem(failPerDataset: 0))
            .RunAsync(new RunRequest("s", "fake", "default", [], null, 0), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*limit-queries*");
    }

    private sealed class FakeSystem(int failPerDataset, string kind = "fake") : ISystemUnderTest, IIndexCachingSystem
    {
        public IReadOnlyDictionary<string, IndexReport> Cached { get; init; } = new Dictionary<string, IndexReport>();
        public List<IReadOnlyList<string>> Saves { get; } = [];
        public bool Restored => Cached.Count > 0;
        public IndexReport? RestoredIndex(string dataset) => Cached.GetValueOrDefault(dataset);

        public Task SaveIndexCacheAsync(IReadOnlyList<string> datasets, CancellationToken ct)
        {
            Saves.Add(datasets);
            return Task.CompletedTask;
        }

        public List<string> Indexed { get; } = [];
        public List<string> Searched { get; } = [];
        public List<string> Used { get; } = [];
        public IReadOnlyList<RetrievedPassage>? Passages { get; init; }
        public string Name => "fake";
        public IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string> { ["kind"] = kind };

        public Task<IndexReport> IndexAsync(EvalDataset dataset, CancellationToken ct)
        {
            Indexed.Add(dataset.Name);
            return Task.FromResult(new IndexReport(dataset.Corpus.Count, failPerDataset, []));
        }

        // Echoes the judged document for each query: q1 → d1, q2 → d2.
        public Task<SearchOutcome> SearchAsync(string dataset, EvalQuery query, int k, CancellationToken ct)
        {
            Searched.Add(query.Id);
            return Task.FromResult(new SearchOutcome([new RankedDoc("d" + query.Id[1..], 1.0)],
                new Trace(TimeSpan.FromMilliseconds(5), new Dictionary<string, TimeSpan>()), null) { Passages = Passages });
        }

        public Task UseSearchConfigAsync(SystemConfig config, CancellationToken ct)
        {
            Used.Add(config.Name);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FileHandler(IReadOnlyDictionary<string, byte[]> extraFiles) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            byte[] body = extraFiles.TryGetValue(request.RequestUri!.AbsolutePath, out byte[]? extra) ? extra : request.RequestUri!.AbsolutePath switch { "/c" => Corpus, "/q" => Queries, _ => QrelsFile };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}

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
    public async Task RunAsync_LimitQueriesZero_ThrowsArgumentException()
    {
        Func<Task> act = () => Runner(new FakeSystem(failPerDataset: 0))
            .RunAsync(new RunRequest("s", "fake", "default", [], null, 0), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*limit-queries*");
    }

    private sealed class FakeSystem(int failPerDataset, string kind = "fake") : ISystemUnderTest
    {
        public List<string> Indexed { get; } = [];
        public List<string> Searched { get; } = [];
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
                new Trace(TimeSpan.FromMilliseconds(5), new Dictionary<string, TimeSpan>()), null));
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

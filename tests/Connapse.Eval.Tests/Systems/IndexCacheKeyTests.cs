using Connapse.Core;
using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Systems;

[Trait("Category", "Unit")]
public class IndexCacheKeyTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "eval-key-" + Guid.NewGuid().ToString("N"));

    private static readonly SystemConfig Plain = new("a", SearchMode.Hybrid, new Dictionary<string, string>());
    private static readonly IReadOnlyDictionary<string, string> NoEnv = new Dictionary<string, string>();

    private static readonly (string, string, IReadOnlyDictionary<string, string>)[] Datasets =
        [("one", "1", new Dictionary<string, string> { ["corpus.jsonl"] = "aa" })];

    public IndexCacheKeyTests()
    {
        Write("src/Connapse.Ingestion/Chunker.cs", "chunk");
        Write("src/Connapse.Search/Fusion.cs", "fuse");
        Write("src/Connapse.Ingestion/bin/Debug/x.dll", "binary");
        Write("tests/Connapse.Eval/Systems/ConnapseSearchSystem.cs", "upload");
        Write("tests/Connapse.Eval/Runs/Scoring.cs", "score");
        Write("Directory.Packages.props", "<Project/>");
    }

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Key(SystemConfig? config = null, IReadOnlyDictionary<string, string>? env = null) =>
        IndexCacheKey.Compute(config ?? Plain, Datasets, IndexCacheKey.SourceHash(_repo), env ?? NoEnv);

    [Theory]
    [InlineData("src/Connapse.Search/Fusion.cs")]
    [InlineData("src/Connapse.Ingestion/bin/Debug/x.dll")]
    [InlineData("tests/Connapse.Eval/Runs/Scoring.cs")]
    public void SourceHash_SearchOnlyOrBuildOutputChanges_KeepsTheKey(string file)
    {
        string before = Key();

        Write(file, "changed");

        Key().Should().Be(before);
    }

    [Theory]
    [InlineData("src/Connapse.Ingestion/Chunker.cs")]
    [InlineData("src/Connapse.Web/Services/UploadService.cs")]
    [InlineData("tests/Connapse.Eval/Systems/ConnapseSearchSystem.cs")]
    [InlineData("Directory.Packages.props")]
    public void SourceHash_IndexShapingSourceChanges_ChangesTheKey(string file)
    {
        string before = Key();

        Write(file, "changed");

        Key().Should().NotBe(before);
    }

    [Fact]
    public void Compute_SearchTimeSettingOrEnvironmentDiffers_KeepsTheKey()
    {
        SystemConfig rerank = new("b", SearchMode.Keyword, new Dictionary<string, string> { ["Knowledge:Search:Reranker"] = "CrossEncoder" });

        Key(rerank).Should().Be(Key());
        Key(env: new Dictionary<string, string> { ["Knowledge__Search__FusionAlpha"] = "0.4", ["PATH"] = "x" }).Should().Be(Key());
    }

    [Fact]
    public void Compute_IndexTimeSettingOrEnvironmentDiffers_ChangesTheKey()
    {
        SystemConfig model = new("b", SearchMode.Hybrid, new Dictionary<string, string> { ["Knowledge:Embedding:Model"] = "m" });

        Key(model).Should().NotBe(Key());
        Key(env: new Dictionary<string, string> { ["Knowledge__Embedding__Model"] = "m" }).Should().NotBe(Key());
    }

    [Fact]
    public void Compute_DatasetFileHashDiffers_ChangesTheKey()
    {
        string source = IndexCacheKey.SourceHash(_repo);
        (string, string, IReadOnlyDictionary<string, string>)[] other =
            [("one", "1", new Dictionary<string, string> { ["corpus.jsonl"] = "bb" })];

        IndexCacheKey.Compute(Plain, other, source, NoEnv).Should().NotBe(IndexCacheKey.Compute(Plain, Datasets, source, NoEnv));
    }
}

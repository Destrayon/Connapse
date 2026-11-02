using System.Diagnostics;
using Connapse.Core;
using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Systems;

[Trait("Category", "Unit")]
public class IndexCacheKeyTests : IDisposable
{
    private protected readonly string _repo = Path.Combine(Path.GetTempPath(), "eval-key-" + Guid.NewGuid().ToString("N"));

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
        Write(".gitignore", "bin/\nobj/\nappdata/\nlogs/\nappsettings.Development.json\n");
        Write("src/Connapse.Web/appsettings.Development.json", "{}");
    }

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    protected void Write(string relative, string content)
    {
        string path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    protected string Key(SystemConfig? config = null, IReadOnlyDictionary<string, string>? env = null) =>
        IndexCacheKey.Compute(config ?? Plain, Datasets, IndexCacheKey.SourceHash(_repo), env ?? NoEnv);

    [Theory]
    [InlineData("src/Connapse.Search/Fusion.cs")]
    [InlineData("src/Connapse.Ingestion/bin/Debug/x.dll")]
    [InlineData("tests/Connapse.Eval/Runs/Scoring.cs")]
    [InlineData("src/Connapse.Web/appdata/keys/key-0c5e.xml")]
    [InlineData("src/Connapse.Web/logs/connapse.log")]
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
    [InlineData("tests/Connapse.Eval/Datasets/RagBenchAdapter.cs")]
    [InlineData("tests/Connapse.Eval/Model/EvalDocument.cs")]
    [InlineData("src/Connapse.Ingestion/Parsers/NewParser.cs")]
    [InlineData("src/Connapse.Web/appsettings.Development.json")]
    [InlineData("Directory.Packages.props")]
    public void SourceHash_IndexShapingSourceChanges_ChangesTheKey(string file)
    {
        string before = Key();

        Write(file, "changed");

        Key().Should().NotBe(before);
    }

    [Fact]
    public void SourceHash_HostWritesItsKeyRingOnStartup_KeepsTheKey()
    {
        // CON-153: the first eval run's host wrote a Data Protection key into appdata/, so the
        // second run on the same code never found the first run's saved index.
        string before = Key();

        Write("src/Connapse.Web/appdata/DataProtection-Keys/key-1.xml", "<key/>");
        Write("src/Connapse.Web/appdata/atlassian-state/listing.json", "{}");

        Key().Should().Be(before);
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

/// <summary>
/// The same cases inside a git work tree, where <see cref="IndexCacheKey.SourceHash"/> takes its file
/// list from git, so <c>.gitignore</c> decides which files are the host's runtime output.
/// </summary>
[Trait("Category", "Unit")]
public class IndexCacheKeyGitRepoTests : IndexCacheKeyTests
{
    public IndexCacheKeyGitRepoTests()
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("init");
        start.ArgumentList.Add("--quiet");
        using Process git = Process.Start(start)!;
        git.WaitForExit();
        git.ExitCode.Should().Be(0);
    }

    [Fact]
    public void SourceHash_FileMatchesAnIgnorePattern_KeepsTheKey()
    {
        Write(".gitignore", "bin/\nobj/\nappdata/\nlogs/\n*.cache\n");
        string before = Key();

        Write("src/Connapse.Ingestion/models.cache", "runtime");

        Key().Should().Be(before);
    }
}

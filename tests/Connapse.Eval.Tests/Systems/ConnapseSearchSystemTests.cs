using Connapse.Core;
using Connapse.Eval.Model;
using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Systems;

[Trait("Category", "Integration")]
public class ConnapseSearchSystemTests
{
    [Fact]
    public async Task SearchAsync_AfterIndexing_RanksMatchingDocumentFirst()
    {
        string root = FindRepoRoot();
        string cacheDir = Path.Combine(Path.GetTempPath(), "eval-it-" + Guid.NewGuid().ToString("N"));
        SystemConfig config = new("it", SearchMode.Hybrid, new Dictionary<string, string>
        {
            ["Knowledge:Embedding:Model"] = "hashing-test",
            ["Knowledge:Embedding:Dimensions"] = "64",
            ["Knowledge:Chunking:Strategy"] = "Recursive",
        });
        EvalDataset dataset = new("it-tiny", "1", ["domain:test"],
        [
            Doc("d1", "Pelicans migrate across the estuary every spring."),
            Doc("d2", "The compiler emits warnings for unused variables."),
            Doc("d3", "Sourdough bread needs a long fermentation."),
        ],
        [new EvalQuery("q1", "compiler warnings unused variables", Split.Test, [])],
        new Qrels());

        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(
            config, Path.Combine(root, "src", "Connapse.Web"), new EmbeddingDiskCache(cacheDir), TextWriter.Null,
            new HashingEmbeddingProvider(), CancellationToken.None);

        IndexReport report = await system.IndexAsync(dataset, CancellationToken.None);
        SearchOutcome outcome = await system.SearchAsync("it-tiny", dataset.Queries[0], 10, CancellationToken.None);

        report.Failed.Should().Be(0);
        outcome.Error.Should().BeNull();
        outcome.Ranked.Should().NotBeEmpty();
        outcome.Ranked[0].DocId.Should().Be("d2");
        system.Describe()["search.mode"].Should().Be("Hybrid");
    }

    private static EvalDocument Doc(string id, string text) =>
        new(id, DocumentKind.Text, null, text, null, new Dictionary<string, string>());

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Connapse.slnx not found above the test output.");
    }
}

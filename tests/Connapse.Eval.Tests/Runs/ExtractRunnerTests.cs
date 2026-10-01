using Connapse.Eval.Checks;
using Connapse.Eval.Cli;
using Connapse.Eval.Runs;
using Connapse.Eval.Systems;
using Connapse.Eval.Tests.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Integration")]
[Collection(EvalHostCollection.Name)]
public class ExtractRunnerTests
{
    /// <summary>
    /// Runs the generated encodings through the real TextParser. Before #594 Latin-1 was decoded as
    /// UTF-8 and indexed garbled, and BOM-less UTF-16 decoded to NUL-filled text that PostgreSQL
    /// rejected. With encoding detection every file reads correctly and is indexed.
    /// </summary>
    [Fact]
    public async Task RunAsync_GeneratedEncodings_DecodesEveryEncoding()
    {
        RepoPaths paths = RepoPaths.Find(AppContext.BaseDirectory);
        string runs = Path.Combine(Path.GetTempPath(), "eval-extract-runs-" + Guid.NewGuid().ToString("N"));
        string embeddingCache = Path.Combine(runs, "embeddings");
        ExtractRunner runner = new(paths, TextWriter.Null, new HttpClient(), (config, _, ct) =>
            ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, new EmbeddingDiskCache(embeddingCache), TextWriter.Null,
                new HashingEmbeddingProvider(), ct), runs);

        RunFolder run = await runner.RunAsync(
            new ExtractRequest("extract-v1", "extract", ["generated-encodings"], null, RealEmbedder: false), CancellationToken.None);

        IReadOnlyList<CheckRecord> checks = run.ReadChecks("generated-encodings");
        Dictionary<string, string> parsed = checks
            .Where(c => c.Type == "present" && c.Level == CheckLevel.Parsed)
            .ToDictionary(c => c.DocId, c => c.Outcome);
        parsed.Should().HaveCount(10);
        parsed.Values.Should().AllBe(CheckOutcome.Pass);
        Dictionary<string, DocumentRecord> documents = run.ReadDocuments("generated-encodings").ToDictionary(d => d.DocId);
        documents["latin1.txt"].Status.Should().Be("Ready");
        documents["utf16le.txt"].Should().Match<DocumentRecord>(d =>
            !d.Stalled && d.Status == "Ready" && d.ChunkCount > 0);
        checks.Where(c => c.Type == ExtractionEvaluator.NoSilentFailure && c.Outcome == CheckOutcome.Fail)
            .Should().BeEmpty();
        ExtractionScoring.Score(run).SilentFailureRate.Should().Be(0);
    }
}

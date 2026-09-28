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
    /// Runs the generated encodings through the real TextParser and pins today's behaviour. UTF-8 and
    /// BOM-marked files read correctly. Latin-1 is decoded as UTF-8: it is indexed garbled and shown as
    /// Ready. UTF-16 without a BOM decodes to text full of NUL characters that PostgreSQL rejects; that
    /// is recorded as a permanent failure, so it fails loudly rather than silently (#562 — it used to
    /// stay Processing while the UI showed it indexed). Fixing encoding detection should flip the
    /// extraction assertions.
    /// </summary>
    [Fact]
    public async Task RunAsync_GeneratedEncodings_RecordsTodaysDecodingBehaviour()
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
        parsed.Where(p => p.Value == CheckOutcome.Pass).Select(p => p.Key).Should().BeEquivalentTo(
            ["utf8.txt", "utf8.md", "utf8-bom.txt", "utf8-bom.md", "utf16le-bom.txt", "utf16le-bom.md"]);
        Dictionary<string, DocumentRecord> documents = run.ReadDocuments("generated-encodings").ToDictionary(d => d.DocId);
        documents["latin1.txt"].Status.Should().Be("Ready");
        documents["utf16le.txt"].Should().Match<DocumentRecord>(d =>
            !d.Stalled && d.Status == "Failed" && d.IngestionState == "FailedPermanent" && d.ChunkCount == 0);
        checks.Where(c => c.Type == ExtractionEvaluator.NoSilentFailure && c.Outcome == CheckOutcome.Fail)
            .Should().BeEmpty();
        ExtractionScoring.Score(run).SilentFailureRate.Should().Be(0);
    }
}

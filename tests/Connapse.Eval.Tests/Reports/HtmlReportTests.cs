using Connapse.Core;
using Connapse.Eval.Model;
using Connapse.Eval.Reports;
using Connapse.Eval.Runs;
using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Reports;

[Trait("Category", "Unit")]
public class HtmlReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eval-html-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static readonly IReadOnlyDictionary<string, TimeSpan> NoStages = new Dictionary<string, TimeSpan>();

    [Fact]
    public void RenderComparison_Rows_ShowDatasetsAndVerdictAndEncodeHtml()
    {
        Comparison c = ComparisonBuilder.Build(
            ComparisonTests.Scores("base<script>", (_, i) => 0.3 + (i % 5 * 0.1), "1", "nanobeir-scifact"),
            ComparisonTests.Scores("cand", (_, i) => 0.5 + (i % 5 * 0.1), "1", "nanobeir-scifact"), false);

        string html = HtmlReport.RenderComparison(c);

        html.Should().Contain("nanobeir-scifact").And.Contain(c.Verdict).And.Contain("base&lt;script&gt;");
        html.Should().NotContain("base<script>");
        html.Should().NotContain("http://").And.NotContain("https://", "the report must be self-contained");
    }

    [Fact]
    public void RenderRun_EncodesQueryTextAndShowsInvalidBannerAndUnreturnedRelevantDoc()
    {
        RunFolder run = RunFolder.Create(_root, "v1", "connapse", "hybrid", "abcdef1234", DateTimeOffset.UnixEpoch);
        Qrels qrels = new();
        qrels.Add("q1", "d1", 1); // relevant, never returned
        qrels.Add("q1", "d2", 1); // relevant, returned

        QueryResult result = new("alpha", "q1", "what is <b>bold</b>?", Split.Test,
            [new RankedDoc("d2", 0.9)], new Trace(TimeSpan.FromMilliseconds(15), NoStages), null);
        run.WriteDataset("alpha", qrels, new Dictionary<string, string>(), [result]);
        run.MarkComplete("alpha");

        RunDatasetInfo alphaInfo = new("alpha", "1", new Dictionary<string, string>(), ["domain:general"], 2, 0, false, 1);
        RunDatasetInfo betaInfo = new("beta", "1", new Dictionary<string, string>(), ["domain:general"], 2, 1, true, 1);
        run.WriteManifest(new RunManifest("abcdef1234", false, "v1", "connapse", "hybrid", "hash", SearchMode.Hybrid,
            new Dictionary<string, string>(), new Dictionary<string, string>(), "box", "os", 8,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [alphaInfo, betaInfo], null, []));

        RunScores scores = Scoring.Score(run);
        string html = HtmlReport.RenderRun(scores, run);

        html.Should().Contain("what is &lt;b&gt;bold&lt;/b&gt;?");
        html.Should().NotContain("what is <b>bold</b>?");
        html.Should().Contain("beta").And.Contain("INVALID");
        html.Should().Contain("grade 1, rank —");
        html.Should().NotContain("http://").And.NotContain("https://", "the report must be self-contained");
    }
}

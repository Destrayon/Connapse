using Connapse.Eval.Reports;
using FluentAssertions;

namespace Connapse.Eval.Tests.Reports;

[Trait("Category", "Unit")]
public class HtmlReportTests
{
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
}

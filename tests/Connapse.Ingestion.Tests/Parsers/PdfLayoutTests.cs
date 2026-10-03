using Connapse.Ingestion.Parsers;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class PdfLayoutTests
{
    /// <summary>
    /// A report page: a running header, two columns of body text, and a page number. The left
    /// column's paragraphs are drawn after the right column's, so content order is not reading order.
    /// </summary>
    internal static TestPdf.Page TwoColumnPage()
    {
        var lines = new List<TestPdf.Line>
        {
            new(72, 760, "Northfield County Water Authority  |  Annual Report 2024"),
        };
        string[] right =
        [
            "Rainfall in the northern catchment was", "eleven percent below the ten year", "average, and the reservoirs closed the",
            "year at sixty two percent of capacity.", "Demand fell for the third year running",
            "as household meters were replaced and", "leaks in the eastern mains were found.",
        ];
        string[] left =
        [
            "The authority supplied water to forty", "one thousand households in 2024 from", "three treatment plants and two wells.",
            "Capital spending reached nine million", "dollars, most of it on the Riverside", "plant filters and the new pumping",
            "station at Mill Creek, which opened", "in September after a long delay.",
        ];
        for (int i = 0; i < right.Length; i++)
            lines.Add(new(320, 700 - i * 14, right[i]));
        for (int i = 0; i < left.Length; i++)
            lines.Add(new(72, 700 - i * 14, left[i]));
        lines.Add(new(300, 40, "7"));
        return new TestPdf.Page(lines);
    }

    [Fact]
    public void Detect_TwoColumnPage_ReadsLeftColumnBeforeRightAndLabelsTheHeader()
    {
        byte[] pdf = TestPdf.Build(TwoColumnPage());

        IReadOnlyList<PdfLayout.Region> regions = PdfLayout.Detect(pdf, 0, threads: 1, CancellationToken.None);

        var text = regions.Where(r => r.Label == "text").ToList();
        PdfLayout.Region leftColumn = text.Single(r => r.Right < 0.5f);
        PdfLayout.Region rightColumn = text.Single(r => r.Left > 0.5f);
        leftColumn.Order.Should().BeLessThan(rightColumn.Order);
        regions.Should().Contain(r => r.Label == "header" && r.Bottom < 0.1f);
        regions.Should().Contain(r => r.Label == "number" && r.Top > 0.9f);
    }

    [Fact]
    public void PostProcess_OverlappingSameClass_KeepsTheHigherScore()
    {
        string[] labels = ["text", "image"];
        PdfLayout.RawBox[] raw =
        [
            new(0, 0.9f, 10, 10, 400, 300, 2),
            new(0, 0.8f, 12, 12, 398, 298, 1),
            new(0, 0.4f, 500, 500, 600, 600, 0),
        ];

        IReadOnlyList<PdfLayout.Region> regions = PdfLayout.PostProcess(raw, labels, 800, 800);

        regions.Should().ContainSingle().Which.Score.Should().Be(0.9f);
    }

    [Fact]
    public void PostProcess_SortsByOrderKeyAndScalesToPageFractions()
    {
        string[] labels = ["text"];
        PdfLayout.RawBox[] raw =
        [
            new(0, 0.9f, 400, 0, 800, 400, 5),
            new(0, 0.9f, 0, 0, 400, 400, 1),
        ];

        IReadOnlyList<PdfLayout.Region> regions = PdfLayout.PostProcess(raw, labels, 800, 800);

        regions.Select(r => r.Left).Should().Equal(0f, 0.5f);
        regions.Select(r => r.Order).Should().Equal(0, 1);
    }
}

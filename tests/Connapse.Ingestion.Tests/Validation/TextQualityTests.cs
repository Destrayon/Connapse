using Connapse.Core;
using Connapse.Ingestion.Pipeline;
using Connapse.Ingestion.Validation;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Validation;

[Trait("Category", "Unit")]
public class TextQualityTests
{
    private static readonly UploadSettings Defaults = new();

    private static string Repeat(string unit, int length) =>
        string.Concat(Enumerable.Repeat(unit, length / unit.Length + 1))[..length];

    [Fact]
    public void DescribeGarbled_OrdinaryProse_IsFine()
    {
        var report = TextQuality.Measure(Repeat("Quarterly revenue grew by 12 percent. ", 2000));

        TextQuality.DescribeGarbled(".pdf", report, Defaults).Should().BeNull();
    }

    [Fact]
    public void DescribeGarbled_PrivateUseGlyphs_FailsAsGarbled()
    {
        // What a PDF font without a Unicode mapping extracts as.
        var report = TextQuality.Measure(Repeat(" ", 2000));

        TextQuality.DescribeGarbled(".pdf", report, Defaults).Should().EndWith("[garbled_text]");
    }

    [Fact]
    public void DescribeGarbled_MostlyPunctuation_FailsAsGarbled()
    {
        var report = TextQuality.Measure(Repeat("!#$%&*+=?@ a", 2000));

        TextQuality.DescribeGarbled(".docx", report, Defaults).Should().EndWith("[garbled_text]");
    }

    [Fact]
    public void DescribeGarbled_TextFiles_AreNeverJudged()
    {
        // Text is decoded byte for byte now, and a symbol-heavy CSV is legitimate.
        var report = TextQuality.Measure(Repeat("|---|---|", 2000));

        TextQuality.DescribeGarbled(".csv", report, Defaults).Should().BeNull();
    }

    [Fact]
    public void DescribeGarbled_TooShortToJudge_IsFine()
    {
        var report = TextQuality.Measure(" 12");

        TextQuality.DescribeGarbled(".pdf", report, Defaults).Should().BeNull();
    }

    [Fact]
    public void DescribePartlyGarbled_SomeUnreadableGlyphs_WarnsButDoesNotFail()
    {
        var report = TextQuality.Measure(Repeat("Readable words here  ", 2000));

        TextQuality.DescribeGarbled(".pdf", report, Defaults).Should().BeNull();
        TextQuality.DescribePartlyGarbled(".pdf", report, Defaults).Should().Contain("unreadable");
    }

    [Fact]
    public void TruncateWarnings_LongList_IsCappedWithACount()
    {
        var warnings = Enumerable.Range(1, 500).Select(i => $"Page {i} contains no extractable text (may be scanned image)").ToList();

        string stored = IngestionPipeline.TruncateWarnings(warnings);

        stored.Length.Should().BeLessThan(4100);
        stored.Should().EndWith("(500 warnings in total)");
    }
}

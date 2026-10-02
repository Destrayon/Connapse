using Connapse.Ingestion.Parsers;
using FluentAssertions;
using Line = Connapse.Ingestion.Parsers.OcrLayout.Line;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class OcrLayoutTests
{
    private static Line At(int left, int top, string text, int width = 400) => new(left, top, left + width, top + 20, text);

    [Fact]
    public void Arrange_TwoColumns_ReadsTheLeftColumnBeforeTheRight()
    {
        // The recogniser reports lines top to bottom across the page, interleaving the columns.
        var lines = new[]
        {
            At(50, 100, "Left one"), At(550, 100, "Right one"),
            At(50, 125, "Left two"), At(550, 125, "Right two"),
        };

        OcrLayout.Arrange(lines).Should().Be("Left one\nLeft two\n\nRight one\nRight two");
    }

    [Fact]
    public void Arrange_FullWidthTitleAboveColumns_ComesFirst()
    {
        var lines = new[]
        {
            At(50, 20, "The title spans both columns", width: 900),
            At(50, 100, "Left one"), At(550, 100, "Right one"),
            At(50, 125, "Left two"), At(550, 125, "Right two"),
        };

        string text = OcrLayout.Arrange(lines);

        text.Should().StartWith("The title spans both columns");
        text.IndexOf("Left two", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Right one", StringComparison.Ordinal));
    }

    [Fact]
    public void Arrange_WordHyphenatedAtALineEnd_IsRejoined()
    {
        var lines = new[] { At(50, 100, "the harbour-"), At(50, 125, "master signed it") };

        OcrLayout.Arrange(lines).Should().Be("the harbourmaster signed it");
    }

    [Fact]
    public void Arrange_HyphenBeforeACapital_IsKept()
    {
        var lines = new[] { At(50, 100, "Anglo-"), At(50, 125, "Saxon charters") };

        OcrLayout.Arrange(lines).Should().Be("Anglo-\nSaxon charters");
    }

    [Fact]
    public void Arrange_GapTallerThanALine_StartsAParagraph()
    {
        var lines = new[] { At(50, 100, "First paragraph."), At(50, 200, "Second paragraph.") };

        OcrLayout.Arrange(lines).Should().Be("First paragraph.\n\nSecond paragraph.");
    }

    [Fact]
    public void Arrange_NoLines_IsEmpty()
    {
        OcrLayout.Arrange([At(0, 0, "  ")]).Should().BeEmpty();
    }
}

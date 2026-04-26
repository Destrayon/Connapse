using Connapse.Core.Interfaces;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Utilities;

[Trait("Category", "Unit")]
public class PragmaticSentenceSegmenterTests
{
    private readonly ISentenceSegmenter _segmenter = new PragmaticSentenceSegmenter();

    [Fact]
    public void Split_EmptyString_ReturnsEmpty()
    {
        _segmenter.Split("").Should().BeEmpty();
    }

    [Fact]
    public void Split_PureWhitespace_ReturnsEmpty()
    {
        _segmenter.Split("   \n  \t  ").Should().BeEmpty();
    }

    [Fact]
    public void Split_TwoSimpleSentences_SplitsCorrectly()
    {
        var result = _segmenter.Split("Hello world. How are you?");
        result.Should().HaveCount(2);
        result[0].Should().Contain("Hello world");
        result[1].Should().Contain("How are you");
    }

    [Fact]
    public void Split_DoesNotSplitOnCommonAbbreviations()
    {
        // The naive regex splits this into 4 fragments. Pragmatic should keep it as one.
        var result = _segmenter.Split("Dr. Smith works for the U.S. government.");
        result.Should().HaveCount(1);
    }

    [Fact]
    public void Split_DoesNotSplitOnDecimalNumbers()
    {
        var result = _segmenter.Split("The value is 3.14 and the price is $1.99.");
        result.Should().HaveCount(1);
    }

    [Fact]
    public void Split_HandlesEllipses()
    {
        var result = _segmenter.Split("He paused... then walked away. The room was silent.");
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Split_HandlesQuestionAndExclamation()
    {
        var result = _segmenter.Split("What now? Run! Then he stopped.");
        result.Should().HaveCount(3);
    }
}

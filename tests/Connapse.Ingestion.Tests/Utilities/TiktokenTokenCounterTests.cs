using Connapse.Core.Interfaces;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Utilities;

[Trait("Category", "Unit")]
public class TiktokenTokenCounterTests
{
    private readonly ITokenCounter _counter = new TiktokenTokenCounter();

    [Fact]
    public void CountTokens_EmptyString_ReturnsZero()
    {
        _counter.CountTokens("").Should().Be(0);
    }

    [Fact]
    public void CountTokens_KnownEnglishPhrase_MatchesTiktokenReference()
    {
        // "Hello world" tokenizes to exactly 2 tokens under cl100k_base.
        _counter.CountTokens("Hello world").Should().Be(2);
    }

    [Fact]
    public void CountTokens_PunctuationAndUnicode_DoesNotThrow()
    {
        Action act = () => _counter.CountTokens("Hello, 世界! 🎉");
        act.Should().NotThrow();
        _counter.CountTokens("Hello, 世界! 🎉").Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetIndexAtTokenCount_ReturnsMonotonicallyIncreasingIndex()
    {
        string text = string.Join(" ", Enumerable.Range(0, 50).Select(i => $"word{i}"));
        int idx10 = _counter.GetIndexAtTokenCount(text, 10);
        int idx20 = _counter.GetIndexAtTokenCount(text, 20);

        idx10.Should().BeGreaterThan(0);
        idx20.Should().BeGreaterThan(idx10);
        idx20.Should().BeLessThanOrEqualTo(text.Length);
    }

    [Fact]
    public void GetIndexAtTokenCount_RequestExceedsText_ReturnsTextLength()
    {
        string text = "short";
        _counter.GetIndexAtTokenCount(text, 9999).Should().Be(text.Length);
    }
}

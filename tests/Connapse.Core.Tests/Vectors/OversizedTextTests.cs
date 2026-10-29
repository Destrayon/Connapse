using Connapse.Storage.Vectors;
using FluentAssertions;

namespace Connapse.Core.Tests.Vectors;

[Trait("Category", "Unit")]
public class OversizedTextTests
{
    [Theory]
    [InlineData("the input length exceeds the context length")]
    [InlineData("input exceeds maximum context length and cannot be truncated further")]
    [InlineData("THE INPUT LENGTH EXCEEDS THE CONTEXT LENGTH")]
    public void IsContextLengthError_OllamaWordings_ReturnsTrue(string message) =>
        OversizedText.IsContextLengthError(message).Should().BeTrue();

    [Theory]
    [InlineData("model \"nomic-embed-text\" not found, try pulling it first")]
    [InlineData("")]
    public void IsContextLengthError_OtherErrors_ReturnsFalse(string message) =>
        OversizedText.IsContextLengthError(message).Should().BeFalse();

    [Fact]
    public void TrySplit_ShorterThanMinimum_ReturnsFalse()
    {
        string text = new('a', OversizedText.MinSplitLength - 1);

        OversizedText.TrySplit(text, out string first, out string second).Should().BeFalse();
        first.Should().BeEmpty();
        second.Should().BeEmpty();
    }

    [Fact]
    public void TrySplit_AtMinimumWithoutWhitespace_CutsAtTheMiddle()
    {
        string text = new string('a', 16) + new string('b', 16);

        OversizedText.TrySplit(text, out string first, out string second).Should().BeTrue();

        first.Should().Be(new string('a', 16));
        second.Should().Be(new string('b', 16));
    }

    [Fact]
    public void TrySplit_WhitespaceBeforeMiddle_CutsThere()
    {
        // Length 40, middle 20; the space at 18 is closer than the one at 23.
        string text = new string('a', 18) + " " + "bbbb" + " " + new string('c', 16);

        OversizedText.TrySplit(text, out string first, out string second).Should().BeTrue();

        first.Should().Be(new string('a', 18));
        second.Should().Be(" bbbb " + new string('c', 16));
    }

    [Fact]
    public void TrySplit_WhitespaceAfterMiddle_CutsThere()
    {
        // Length 40, middle 20; the only space is at 22.
        string text = new string('a', 22) + " " + new string('b', 17);

        OversizedText.TrySplit(text, out string first, out string second).Should().BeTrue();

        first.Should().Be(new string('a', 22));
        second.Should().Be(" " + new string('b', 17));
    }

    [Fact]
    public void TrySplit_WhitespaceAfterMiddleIsCloser_IgnoresFartherWhitespaceBefore()
    {
        // Length 40, middle 20; the space at 22 is closer than the one at 16.
        string text = new string('a', 16) + " " + "bbbbb" + " " + new string('c', 17);

        OversizedText.TrySplit(text, out string first, out _).Should().BeTrue();

        first.Should().Be(new string('a', 16) + " bbbbb");
    }

    [Fact]
    public void TrySplit_WhitespaceEquallyFar_PrefersTheEarlierOne()
    {
        // Length 40, middle 20; spaces at 18 and 22.
        string text = new string('a', 18) + " bbb " + new string('c', 17);

        OversizedText.TrySplit(text, out string first, out _).Should().BeTrue();

        first.Should().Be(new string('a', 18));
    }

    [Fact]
    public void TrySplit_WhitespaceExactlyAQuarterAway_IsUsed()
    {
        // Length 40, middle 20, a quarter is 10: the space at 30 is just within reach.
        string text = new string('a', 30) + " " + new string('b', 9);

        OversizedText.TrySplit(text, out string first, out _).Should().BeTrue();

        first.Should().Be(new string('a', 30));
    }

    [Fact]
    public void TrySplit_WhitespaceOutsideTheMiddleHalf_IsIgnored()
    {
        // Length 40: spaces at 2 and 37 would leave a tiny piece, so the cut is at the middle.
        string text = "aa " + new string('b', 34) + " cc";

        OversizedText.TrySplit(text, out string first, out string second).Should().BeTrue();

        first.Should().HaveLength(20);
        (first + second).Should().Be(text);
    }

    [Fact]
    public void TrySplit_MiddleInsideSurrogatePair_KeepsThePairTogether()
    {
        // "😀" is two chars; placed so the middle (20) lands on its low surrogate.
        string text = new string('a', 19) + "😀" + new string('b', 19);

        OversizedText.TrySplit(text, out string first, out string second).Should().BeTrue();

        first.Should().Be(new string('a', 19) + "😀");
        second.Should().Be(new string('b', 19));
    }

    [Fact]
    public void Pool_OnePiece_ReturnsItsVectorUnchanged()
    {
        float[] vector = [3f, 4f];

        OversizedText.Pool([(vector, 10)]).Should().BeSameAs(vector);
    }

    [Fact]
    public void Pool_SeveralPieces_WeightsByLengthAndRescalesToUnitLength()
    {
        float[] pooled = OversizedText.Pool([([1f, 0f], 3), ([0f, 1f], 1)]);

        float scale = (float)Math.Sqrt(10);
        pooled[0].Should().BeApproximately(3f / scale, 1e-6f);
        pooled[1].Should().BeApproximately(1f / scale, 1e-6f);
    }

    [Fact]
    public void Pool_PiecesCancelOut_ReturnsZeroVector()
    {
        float[] pooled = OversizedText.Pool([([1f, 0f], 1), ([-1f, 0f], 1)]);

        pooled.Should().Equal(0f, 0f);
    }
}

using System.Text;
using Connapse.Core;
using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class StorableTextTests
{
    [Fact]
    public void Clean_OrdinaryText_ReturnsTheSameInstance()
    {
        const string text = "Plain text with a valid pair: \U0001D465 and café.";

        StorableText.Clean(text).Should().BeSameAs(text);
    }

    [Fact]
    public void Clean_Nul_IsRemoved() =>
        StorableText.Clean("a\0b\0\0c").Should().Be("abc");

    [Fact]
    public void Clean_LoneHighSurrogate_BecomesReplacementCharacter() =>
        StorableText.Clean("x\uD835y").Should().Be("x�y");

    [Fact]
    public void Clean_LoneLowSurrogate_BecomesReplacementCharacter() =>
        StorableText.Clean("x\uDC65y").Should().Be("x�y");

    [Fact]
    public void Clean_HighSurrogateAtTheEnd_BecomesReplacementCharacter() =>
        StorableText.Clean("end\uD835").Should().Be("end�");

    [Fact]
    public void Clean_ValidPairsAroundAProblem_AreKept() =>
        StorableText.Clean("\U0001D465\0\U0001D466").Should().Be("\U0001D465\U0001D466");

    [Fact]
    public void Uncase_ChunkEndingInHalfASurrogatePair_DoesNotThrow()
    {
        // A chunker cut "𝑥" (U+1D465) in half; Normalize used to throw on what was left.
        var act = () => EmbeddingText.Uncase("Let \uD835");

        act.Should().NotThrow().Which.Should().Be("let �");
    }

    [Fact]
    public void Clean_Result_EncodesToUtf8WithoutThrowing()
    {
        // The failure #334 hit: Npgsql's UTF-8 encoder rejects lone surrogates.
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        var act = () => strict.GetBytes(StorableText.Clean("math \uD835 then \uDC00 done\0"));

        act.Should().NotThrow();
    }
}

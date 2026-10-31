using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class DocumentSetHasherTests
{
    [Fact]
    public void Finish_SeveralDocuments_MatchesHashOfJoinedLines()
    {
        using var hasher = new DocumentSetHasher();
        hasher.Add("a", "h1");
        hasher.Add("b", "h2");
        hasher.Add("c", null);

        DocumentSetFingerprint fingerprint = hasher.Finish();

        fingerprint.DocumentCount.Should().Be(3);
        fingerprint.Hash.Should().Be(HexHash.Sha256("a|h1\nb|h2\nc|"));
    }

    [Fact]
    public void Finish_OneDocument_HasNoNewline()
    {
        using var hasher = new DocumentSetHasher();
        hasher.Add("a", "h1");

        hasher.Finish().Hash.Should().Be(HexHash.Sha256("a|h1"));
    }

    [Fact]
    public void Finish_NoDocuments_CountsZeroAndHashesEmptyString()
    {
        using var hasher = new DocumentSetHasher();

        DocumentSetFingerprint fingerprint = hasher.Finish();

        fingerprint.DocumentCount.Should().Be(0);
        fingerprint.Hash.Should().Be(HexHash.Sha256(string.Empty));
    }

    [Fact]
    public void Finish_ContentHashChanges_HashChanges()
    {
        using var before = new DocumentSetHasher();
        before.Add("a", "h1");
        using var after = new DocumentSetHasher();
        after.Add("a", "h2");

        after.Finish().Hash.Should().NotBe(before.Finish().Hash);
    }
}

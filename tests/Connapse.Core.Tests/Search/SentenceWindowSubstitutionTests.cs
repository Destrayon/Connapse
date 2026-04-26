using Connapse.Core;
using Connapse.Search.Hybrid;
using FluentAssertions;

namespace Connapse.Core.Tests.Search;

[Trait("Category", "Unit")]
public class SentenceWindowSubstitutionTests
{
    [Fact]
    public void Substitute_HitWithWindowMetadata_ReplacesContent()
    {
        var hit = new SearchHit(
            ChunkId: "c1",
            DocumentId: "d1",
            Content: "Beta.",
            Score: 0.9f,
            Metadata: new Dictionary<string, string>
            {
                ["window"] = "Alpha. Beta. Gamma.",
                ["original_text"] = "Beta."
            });

        IReadOnlyList<SearchHit> result = SentenceWindowSubstitution.SubstituteIfEnabled(
            new[] { hit },
            substituteOnSearch: true);

        result.Should().HaveCount(1);
        result[0].Content.Should().Be("Alpha. Beta. Gamma.");
    }

    [Fact]
    public void Substitute_HitWithoutWindow_PreservesContent()
    {
        var hit = new SearchHit(
            ChunkId: "c1",
            DocumentId: "d1",
            Content: "Just text.",
            Score: 0.9f,
            Metadata: new Dictionary<string, string>());

        IReadOnlyList<SearchHit> result = SentenceWindowSubstitution.SubstituteIfEnabled(
            new[] { hit },
            substituteOnSearch: true);

        result[0].Content.Should().Be("Just text.");
    }

    [Fact]
    public void Substitute_FlagDisabled_PreservesContentEvenWithWindow()
    {
        var hit = new SearchHit(
            ChunkId: "c1",
            DocumentId: "d1",
            Content: "Beta.",
            Score: 0.9f,
            Metadata: new Dictionary<string, string>
            {
                ["window"] = "Alpha. Beta. Gamma."
            });

        IReadOnlyList<SearchHit> result = SentenceWindowSubstitution.SubstituteIfEnabled(
            new[] { hit },
            substituteOnSearch: false);

        result[0].Content.Should().Be("Beta.");
    }

    [Fact]
    public void Substitute_EmptyWindowValue_PreservesContent()
    {
        var hit = new SearchHit(
            ChunkId: "c1",
            DocumentId: "d1",
            Content: "Original.",
            Score: 0.9f,
            Metadata: new Dictionary<string, string>
            {
                ["window"] = "   "  // whitespace-only — should not substitute
            });

        IReadOnlyList<SearchHit> result = SentenceWindowSubstitution.SubstituteIfEnabled(
            new[] { hit },
            substituteOnSearch: true);

        result[0].Content.Should().Be("Original.");
    }
}

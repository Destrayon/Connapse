using Connapse.Storage.Vectors;
using FluentAssertions;

namespace Connapse.Core.Tests.Vectors;

[Trait("Category", "Unit")]
public class VectorColumnManagerTests
{
    private static readonly Guid Container = Guid.Parse("6f1c2a44-0000-0000-0000-000000000001");

    [Fact]
    public void GetIndexName_IsFixedLengthAndValidForAnyModelId()
    {
        string name = VectorColumnManager.GetIndexName(Container, "hf.co/org/Some Model:Q8_0-recipe-1a2b3c4d", 768);

        name.Should().MatchRegex("^ix_cv_hnsw_[0-9a-f]{32}_[0-9a-f]{8}$").And.HaveLength(52);
    }

    [Fact]
    public void GetIndexName_DiffersByContainerModelAndDimensions()
    {
        string name = VectorColumnManager.GetIndexName(Container, "nomic-embed-text", 768);

        VectorColumnManager.GetIndexName(Guid.NewGuid(), "nomic-embed-text", 768).Should().NotBe(name);
        VectorColumnManager.GetIndexName(Container, "nomic-embed-text-recipe-1a2b3c4d", 768).Should().NotBe(name);
        VectorColumnManager.GetIndexName(Container, "nomic-embed-text", 512).Should().NotBe(name);
        VectorColumnManager.GetIndexName(Container, "nomic-embed-text", 768).Should().Be(name);
    }

    [Fact]
    public void Predicate_QuotesTheModelIdInsteadOfDroppingCharacters()
    {
        // The old sanitiser dropped ':' and '/', so an index for "nomic-embed-text:latest" matched no rows.
        VectorColumnManager.Predicate(Container, "nomic-embed-text:latest", 768)
            .Should().Be($"owner_id = '{Container}'::uuid AND model_id = 'nomic-embed-text:latest' AND vector_dims(embedding) = 768");
        VectorColumnManager.Predicate(Container, "it's", 768)
            .Should().Contain("model_id = 'it''s'");
    }
}

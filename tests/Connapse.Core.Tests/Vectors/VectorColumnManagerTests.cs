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
        string name = VectorColumnManager.GetIndexName(Container, "hf.co/org/Some Model:Q8_0-recipe-1a2b3c4d");

        name.Should().MatchRegex("^ix_cv_hnsw_[0-9a-f]{32}_[0-9a-f]{8}$").And.HaveLength(52);
    }

    [Fact]
    public void GetIndexName_DiffersByContainerAndModel()
    {
        string name = VectorColumnManager.GetIndexName(Container, "nomic-embed-text");

        VectorColumnManager.GetIndexName(Guid.NewGuid(), "nomic-embed-text").Should().NotBe(name);
        VectorColumnManager.GetIndexName(Container, "nomic-embed-text-recipe-1a2b3c4d").Should().NotBe(name);
        VectorColumnManager.GetIndexName(Container, "nomic-embed-text").Should().Be(name);
    }

    [Fact]
    public void Predicate_QuotesTheModelIdInsteadOfDroppingCharacters()
    {
        // The old sanitiser dropped ':' and '/', so an index for "nomic-embed-text:latest" matched no rows.
        VectorColumnManager.Predicate(Container, "nomic-embed-text:latest")
            .Should().Be($"owner_id = '{Container}'::uuid AND model_id = 'nomic-embed-text:latest'");
        VectorColumnManager.Predicate(Container, "it's")
            .Should().EndWith("model_id = 'it''s'");
    }
}

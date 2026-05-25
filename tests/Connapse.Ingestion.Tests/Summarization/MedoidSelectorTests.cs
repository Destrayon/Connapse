using Connapse.Ingestion.Summarization;
using FluentAssertions;
using Xunit;

namespace Connapse.Ingestion.Tests.Summarization;

[Trait("Category", "Unit")]
public class MedoidSelectorTests
{
    [Fact]
    public void Select_Returns_K_DistinctMedoids()
    {
        // 100 random embeddings in 8-dim space
        Random rng = new Random(42);
        var input = Enumerable.Range(0, 100)
            .Select(i => (Id: Guid.NewGuid(), Embedding: RandomVec(rng, 8)))
            .ToList();

        var medoids = MedoidSelector.SelectFarthestFirst(input, k: 10);

        medoids.Should().HaveCount(10);
        medoids.Select(m => m.Id).Distinct().Should().HaveCount(10);
    }

    [Fact]
    public void Select_ReturnsAll_WhenKExceedsInputSize()
    {
        Random rng = new Random(1);
        var input = Enumerable.Range(0, 3)
            .Select(i => (Id: Guid.NewGuid(), Embedding: RandomVec(rng, 4)))
            .ToList();

        var medoids = MedoidSelector.SelectFarthestFirst(input, k: 10);

        medoids.Should().HaveCount(3);
    }

    [Fact]
    public void Select_AssignsAllDocsToNearestMedoid_CountsCorrect()
    {
        Random rng = new Random(7);
        var input = Enumerable.Range(0, 60)
            .Select(i => (Id: Guid.NewGuid(), Embedding: RandomVec(rng, 6)))
            .ToList();

        var result = MedoidSelector.SelectFarthestFirstWithAssignments(input, k: 6);

        result.Medoids.Should().HaveCount(6);
        result.Medoids.Sum(m => m.ClusterSize).Should().Be(60);
    }

    private static float[] RandomVec(Random rng, int dim) =>
        Enumerable.Range(0, dim).Select(_ => (float)rng.NextDouble()).ToArray();
}

using Connapse.Search.Keyword;
using FluentAssertions;

namespace Connapse.Core.Tests.Search;

[Trait("Category", "Unit")]
public class Bm25PruningTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Clauses_RandomBounds_EveryChunkAboveTheThresholdMatchesAClause(int seed)
    {
        var random = new Random(seed);
        var misses = new List<string>();
        for (int trial = 0; trial < 200; trial++)
        {
            int termCount = random.Next(1, 6);
            var bounds = Enumerable.Range(0, termCount)
                .Select(_ =>
                {
                    double weight = random.NextDouble() * 5;
                    return Enumerable.Range(0, random.Next(1, 6))
                        .Select(j => weight * (j + 1) / (j + 2.0))
                        .ToArray();
                })
                .ToList();
            double threshold = random.NextDouble() * bounds.Sum(b => b[^1]);

            var clauses = Bm25Pruning.Clauses(bounds, threshold, slack: 0.3, maxClauses: 10_000);
            if (clauses is null)
                continue; // "score everything" is always safe

            foreach (int[] levels in AllLevelVectors(bounds))
            {
                double bound = levels.Select((l, t) => l < 0 ? 0 : bounds[t][l]).Sum();
                if (bound <= threshold)
                    continue;

                if (!clauses.Any(c => c.All(p => levels[p.Key] >= p.Value)))
                    misses.Add($"trial {trial}: levels [{string.Join(",", levels)}] bound {bound} > {threshold}");
            }
        }

        misses.Should().BeEmpty();
    }

    [Fact]
    public void Clauses_DominatedClauses_AreDropped()
    {
        List<double[]> bounds = [[1.0, 2.0], [1.0, 2.0]];

        var clauses = Bm25Pruning.Clauses(bounds, threshold: 1.5, slack: 0)!;

        // Either term at level 1 alone (2.0) or both at level 0 (2.0) exceed 1.5; nothing stricter.
        clauses.Select(c => string.Join(",", c.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}")))
            .Should().BeEquivalentTo(["0:1", "1:1", "0:0,1:0"]);
    }

    [Fact]
    public void Clauses_ThresholdWithinSlack_ScoresEverything()
    {
        List<double[]> bounds = [[0.01], [0.02]];

        Bm25Pruning.Clauses(bounds, threshold: 1.0, slack: 0.3).Should().BeNull();
    }

    [Fact]
    public void LevelBounds_StopAtTheTermsHighestFrequency()
    {
        var term = new Bm25Pruning.Term("cat", Weight: 2.0, MaxTf: 3, MinLen: 10);

        double[] bounds = Bm25Pruning.LevelBounds(term, k1: 1.2, b: 0.75, avgdl: 10);

        // Levels 0 (f = 1), 1 (f = 2) and 2 (f = 3, capped by MaxTf).
        bounds.Should().HaveCount(3);
        bounds[0].Should().BeApproximately(2.0 * 1 / (1 + 1.2), 1e-12);
        bounds[2].Should().BeApproximately(2.0 * 3 / (3 + 1.2), 1e-12);
    }

    [Fact]
    public void ToTsQuery_Clauses_QuotesLexemesAndMarkers()
    {
        var terms = new[] { new Bm25Pruning.Term("o'brien", 1, 1, 1), new Bm25Pruning.Term("cat", 1, 5, 1) };
        var clauses = new List<Dictionary<int, int>> { new() { [0] = 0, [1] = 2 }, new() { [1] = 4 } };

        Bm25Pruning.ToTsQuery(clauses, terms)
            .Should().Be("('o''brien' & 'cat\u001F3') | ('cat\u001F5')");
    }

    private static IEnumerable<int[]> AllLevelVectors(List<double[]> bounds)
    {
        IEnumerable<int[]> vectors = [[]];
        foreach (double[] levels in bounds)
            vectors = vectors.SelectMany(v => Enumerable.Range(-1, levels.Length + 1).Select(l => (int[])[.. v, l]));
        return vectors;
    }
}

using Connapse.Core;
using Connapse.Search.Keyword;
using Connapse.Search.Vector;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// The C#-side merges order tied scores by path, then chunk index, then chunk id, as the SQL cut-offs do (CON-158),
/// whatever order the rows arrive in.
/// </summary>
[Trait("Category", "Unit")]
public class SearchTieBreakTests
{
    private static readonly Guid IdA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid IdB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static KeywordSearchService.Bm25Row Row(Guid id, string path, int chunkIndex, double score) =>
        new(id, Guid.Empty, "", chunkIndex, score, "", null, Guid.Empty, path);

    private static VectorSearchResult Result(string id, string path, int chunkIndex, float score) =>
        new(id, score, new Dictionary<string, string> { ["path"] = path, ["chunkIndex"] = chunkIndex.ToString() });

    [Fact]
    public void KeywordRanked_TiedScores_OrdersByPathThenChunkThenId()
    {
        KeywordSearchService.Bm25Row[] rows =
        [
            Row(IdA, "/b.txt", 0, 1.0),
            Row(IdB, "/a.txt", 1, 1.0),
            Row(IdB, "/a.txt", 0, 1.0),
            Row(IdA, "/a.txt", 0, 1.0),
            Row(IdA, "/z.txt", 0, 2.0),
        ];

        var ranked = KeywordSearchService.Ranked(rows).ToList();

        ranked.Should().Equal(rows[4], rows[3], rows[2], rows[1], rows[0]);
    }

    [Fact]
    public void KeywordRanked_PathsCompareOrdinally()
    {
        // Ordinal puts "B" before "a"; a culture-aware compare would not, and would disagree with COLLATE "C".
        KeywordSearchService.Bm25Row[] rows = [Row(IdA, "/a.txt", 0, 1.0), Row(IdB, "/B.txt", 0, 1.0)];

        KeywordSearchService.Ranked(rows).Select(r => r.Path).Should().Equal("/B.txt", "/a.txt");
    }

    [Fact]
    public void VectorRanked_TiedScores_OrdersByPathThenChunkThenId()
    {
        VectorSearchResult[] results =
        [
            Result("b", "/b.txt", 0, 0.5f),
            Result("c", "/a.txt", 10, 0.5f),
            Result("b", "/a.txt", 2, 0.5f),
            Result("z", "/a.txt", 0, 0.5f),
            Result("a", "/a.txt", 0, 0.5f),
            Result("y", "/z.txt", 0, 0.9f),
        ];

        var ranked = VectorSearchService.Ranked(results).ToList();

        // Chunk index compares as a number, so 2 comes before 10.
        ranked.Should().Equal(results[5], results[4], results[3], results[2], results[1], results[0]);
    }
}

using Connapse.Core;
using Connapse.Eval.Model;
using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Systems;

[Trait("Category", "Unit")]
public class HitCollapserTests
{
    private static SearchHit Hit(string documentId, float score) =>
        new(Guid.NewGuid().ToString(), documentId, "chunk", score, new Dictionary<string, string>());

    private static readonly Dictionary<string, string> Map = new() { ["c1"] = "d1", ["c2"] = "d2", ["c3"] = "d3" };

    [Fact]
    public void Collapse_SeveralChunksPerDocument_KeepsBestRankedChunk()
    {
        IReadOnlyList<RankedDoc> ranked =
            HitCollapser.Collapse([Hit("c2", 0.9f), Hit("c1", 0.8f), Hit("c2", 0.7f), Hit("c3", 0.6f)], Map, 10);

        ranked.Should().Equal(new RankedDoc("d2", 0.9f), new RankedDoc("d1", 0.8f), new RankedDoc("d3", 0.6f));
    }

    [Fact]
    public void Collapse_MoreDocumentsThanK_StopsAtK()
    {
        HitCollapser.Collapse([Hit("c1", 0.9f), Hit("c2", 0.8f), Hit("c3", 0.7f)], Map, 2).Should().HaveCount(2);
    }

    [Fact]
    public void Collapse_UnknownDocument_IsSkipped()
    {
        HitCollapser.Collapse([Hit("other", 0.9f), Hit("c1", 0.8f)], Map, 10)
            .Select(r => r.DocId).Should().Equal("d1");
    }
}

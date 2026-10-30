using System.Text.Json;
using Connapse.Core;
using Connapse.Search.Hybrid;
using Connapse.Search.Links;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Search;

[Trait("Category", "Unit")]
public class LinkSearchTests
{
    private static SearchHit Hit(string chunk, string doc, float score) => new(chunk, doc, "", score, []);

    private static VisibleNode Node(string id) => new(id, [], id, [], []);

    private static VisibleEdge Edge(string from, string to) => new($"{from}-{to}", from, to, "closes", [], []);

    [Fact]
    public void SeedScores_TopDocumentsNormalised_BestChunkPerDocument()
    {
        List<SearchHit> hits = [Hit("c1", "a", 0.9f), Hit("c2", "a", 0.5f), Hit("c3", "b", 0.7f), Hit("c4", "c", 0.1f)];

        Dictionary<string, float> seeds = LinkSearch.SeedScores(hits, seeds: 2);

        seeds.Should().HaveCount(2);
        seeds["a"].Should().BeApproximately(1f, 1e-6f);
        seeds["b"].Should().BeApproximately(0.75f, 1e-6f);
    }

    [Fact]
    public void PropagateScores_TwoHops_HalvesPerExtraHopAndKeepsBestPath()
    {
        LinkExpansion walk = new(
            [Node("s1"), Node("s2"), Node("x"), Node("y")],
            [Edge("s1", "x"), Edge("x", "y"), Edge("s2", "y")]);

        Dictionary<string, float> scores = LinkSearch.PropagateScores(
            new Dictionary<string, float> { ["s1"] = 1f, ["s2"] = 0.4f }, walk, maxHops: 2);

        scores["x"].Should().BeApproximately(1f, 1e-6f);
        // y: 0.4 directly from s2, or 1 × 0.5 through x; the better path wins.
        scores["y"].Should().BeApproximately(0.5f, 1e-6f);
    }

    [Fact]
    public void PropagateScores_WalkOutAndBack_SeedDoesNotScoreItself()
    {
        LinkExpansion walk = new([Node("s"), Node("x")], [Edge("s", "x")]);

        LinkSearch.PropagateScores(new Dictionary<string, float> { ["s"] = 1f }, walk, maxHops: 2)
            .Keys.Should().BeEquivalentTo(["x"]);
    }

    [Fact]
    public void PropagateScores_LinkedSeeds_ScoreEachOther()
    {
        LinkExpansion walk = new([Node("a"), Node("b")], [Edge("a", "b")]);

        Dictionary<string, float> scores = LinkSearch.PropagateScores(
            new Dictionary<string, float> { ["a"] = 1f, ["b"] = 0.6f }, walk, maxHops: 2);

        scores["a"].Should().BeApproximately(0.6f, 1e-6f);
        scores["b"].Should().BeApproximately(1f, 1e-6f);
    }

    [Fact]
    public void PropagateScores_OneHop_DoesNotGoFurther()
    {
        LinkExpansion walk = new([Node("s"), Node("x"), Node("y")], [Edge("s", "x"), Edge("x", "y")]);

        LinkSearch.PropagateScores(new Dictionary<string, float> { ["s"] = 1f }, walk, maxHops: 1)
            .Keys.Should().BeEquivalentTo(["x"]);
    }

    [Fact]
    public void PropagateScores_LightLinks_PassOnLessOnEveryHop()
    {
        // s -links-to-> x -labelled-> y, and s -parent-> p: page links and labels count for less (CON-34).
        LinkExpansion walk = new(
            [Node("s"), Node("x"), Node("y"), Node("p")],
            [new("e1", "s", "x", "links-to", [], []), new("e2", "x", "y", "labelled", [], []), new("e3", "s", "p", "parent", [], [])]);

        Dictionary<string, float> scores = LinkSearch.PropagateScores(new Dictionary<string, float> { ["s"] = 1f }, walk, maxHops: 2);

        scores["p"].Should().BeApproximately(1f, 1e-6f);
        scores["x"].Should().BeApproximately(0.5f, 1e-6f);
        scores["y"].Should().BeApproximately(0.125f, 1e-6f);
    }

    [Fact]
    public void PropagateScores_SeedHiddenFromReader_StartsNothing()
    {
        // The walk did not return s (its reader cannot see it), so no score flows from it.
        LinkExpansion walk = new([Node("x")], []);

        LinkSearch.PropagateScores(new Dictionary<string, float> { ["s"] = 1f }, walk, maxHops: 2)
            .Should().BeEmpty();
    }

    // Ids that sort the opposite way to their paths, as fresh random GUIDs can on any index (CON-152).
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["ffff"] = "/jira/FLINK-1.md",
        ["cccc"] = "/jira/FLINK-2.md",
        ["aaaa"] = "/jira/FLINK-3.md",
    };

    [Fact]
    public void TopReached_TiedScores_KeepsTheFirstPathsNotTheFirstIds()
    {
        Dictionary<string, float> scores = new() { ["aaaa"] = 0.5f, ["cccc"] = 0.5f, ["ffff"] = 0.5f };

        LinkSearch.TopReached(scores, Paths, max: 2).Keys.Should().BeEquivalentTo(["ffff", "cccc"]);
    }

    [Fact]
    public void TopReached_InputOrder_DoesNotChangeTheCut()
    {
        KeyValuePair<string, float>[] scores = [new("aaaa", 0.5f), new("cccc", 0.5f), new("ffff", 0.5f)];

        LinkSearch.TopReached(scores.Reverse(), Paths, max: 2).Keys
            .Should().BeEquivalentTo(LinkSearch.TopReached(scores, Paths, max: 2).Keys);
    }

    [Fact]
    public void TopReached_HigherScore_BeatsAnEarlierPath()
    {
        Dictionary<string, float> scores = new() { ["aaaa"] = 0.9f, ["cccc"] = 0.5f, ["ffff"] = 0.5f };

        Dictionary<string, float> reached = LinkSearch.TopReached(scores, Paths, max: 2);

        reached.Keys.Should().BeEquivalentTo(["aaaa", "ffff"]);
        reached["aaaa"].Should().Be(0.9f);
    }

    [Fact]
    public void TopReached_SamePath_FallsBackToTheId()
    {
        // Two containers can each hold a document at one path; the id still settles it within an index.
        Dictionary<string, float> scores = new() { ["bbbb"] = 0.5f, ["aaaa"] = 0.5f };
        Dictionary<string, string> paths = new() { ["aaaa"] = "/same.md", ["bbbb"] = "/same.md" };

        LinkSearch.TopReached(scores, paths, max: 1).Keys.Should().Equal("aaaa");
    }

    private static LinkSearch.ChunkRow Row(string doc, int index, string path) => new()
    {
        ChunkId = Guid.Parse($"00000000-0000-0000-{index:0000}-{doc}"),
        DocumentId = Guid.Parse($"00000000-0000-0000-0000-{doc}"),
        ChunkIndex = index,
        Path = path,
    };

    [Fact]
    public void BestChunks_RowsInAnyOrder_ComeOutInPathOrder()
    {
        // The documents' ids sort the opposite way to their paths; SQL returns rows in no set order.
        List<LinkSearch.ChunkRow> rows =
        [
            Row("000000000003", 0, "/a.md"), Row("000000000002", 0, "/b.md"), Row("000000000001", 0, "/c.md"),
        ];
        Dictionary<string, float> none = [];

        List<string> forward = LinkSearch.BestChunks(rows, none).Select(h => h.DocumentId).ToList();
        List<string> backward = LinkSearch.BestChunks(Enumerable.Reverse(rows), none).Select(h => h.DocumentId).ToList();

        forward.Should().Equal(
            "00000000-0000-0000-0000-000000000003",
            "00000000-0000-0000-0000-000000000002",
            "00000000-0000-0000-0000-000000000001");
        backward.Should().Equal(forward);
    }

    [Fact]
    public void BestChunks_PerDocument_HighestScoreThenFirstChunk()
    {
        List<LinkSearch.ChunkRow> rows =
        [
            Row("000000000001", 2, "/a.md"), Row("000000000001", 1, "/a.md"), Row("000000000002", 3, "/b.md"),
            Row("000000000002", 4, "/b.md"),
        ];
        Dictionary<string, float> scores = new() { [rows[3].ChunkId.ToString()] = 0.7f };

        List<SearchHit> best = LinkSearch.BestChunks(rows, scores);

        best.Select(h => h.ChunkId).Should().Equal(rows[1].ChunkId.ToString(), rows[3].ChunkId.ToString());
        best[1].Score.Should().Be(0.7f);
        best[0].Metadata["path"].Should().Be("/a.md");
    }

    [Fact]
    public void Blend_LinkedDocumentAddedAndTagged_OthersKeepTheirOrder()
    {
        List<SearchHit> hits = [Hit("c1", "a", 0.9f), Hit("c2", "b", 0.6f), Hit("c3", "c", 0.3f)];
        // z's ordinary score normalises to 0.5, so it scores 0.25 + 0.5 = 0.75 against a's 0.5.
        List<SearchHit> extras = [Hit("c9", "z", 0.6f)];

        List<SearchHit> blended = LinkSearch.Blend(hits, extras, new Dictionary<string, float> { ["z"] = 1f }, weight: 0.5f);

        blended.Select(h => h.ChunkId).Should().Equal("c9", "c1", "c2", "c3");
        blended[0].Metadata["source"].Should().Be("links");
        blended[0].Metadata["linkScore"].Should().Be("1.0000");
        blended.Where(h => h.ChunkId != "c9").Select(h => h.ChunkId).Should().Equal("c1", "c2", "c3");
    }

    [Fact]
    public void Blend_ZeroWeight_KeepsOrdinaryOrder()
    {
        List<SearchHit> hits = [Hit("c1", "a", 0.9f), Hit("c2", "b", 0.6f)];

        LinkSearch.Blend(hits, [], new Dictionary<string, float> { ["b"] = 1f }, weight: 0f)
            .Select(h => h.ChunkId).Should().Equal("c1", "c2");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SideNormalizer_MatchesFusionForHitsOnTheSide(bool dbsf)
    {
        List<SearchHit> side = [Hit("a", "a", 0.2f), Hit("b", "b", 0.5f), Hit("c", "c", 0.9f), Hit("d", "d", 0.4f)];
        List<SearchHit> fused = dbsf
            ? HybridSearchService.FuseResultsDbsf(side, [], 1f)
            : HybridSearchService.FuseResults(side, [], 1f);
        Func<float, float> normalise = HybridSearchService.SideNormalizer(side, dbsf);

        foreach (SearchHit hit in side)
            normalise(hit.Score).Should().BeApproximately(fused.Single(f => f.ChunkId == hit.ChunkId).Score, 1e-5f);
    }

    [Fact]
    public void SideNormalizer_ScoreOutsideTheSide_IsClamped()
    {
        Func<float, float> normalise = HybridSearchService.SideNormalizer([Hit("a", "a", 0.2f), Hit("b", "b", 0.6f)], dbsf: false);

        normalise(0.1f).Should().Be(0f);
        normalise(0.9f).Should().Be(1f);
    }

    [Theory]
    [InlineData(true, true, SearchMode.Hybrid, SearchMode.Hybrid)]
    [InlineData(true, false, SearchMode.Hybrid, SearchMode.Semantic)]
    [InlineData(false, true, SearchMode.Hybrid, SearchMode.Keyword)]
    [InlineData(false, false, SearchMode.Keyword, SearchMode.Keyword)]
    [InlineData(true, false, SearchMode.Keyword, SearchMode.Semantic)]
    public void Mode_OwnerChoices_PickTheSearchesThatRun(bool vector, bool keyword, SearchMode requested, SearchMode expected) =>
        SourceSearchChoices.Mode(requested, new SearchSettings { UseVectorSearch = vector, UseKeywordSearch = keyword })
            .Should().Be(expected);

    [Fact]
    public void Parse_OverrideJson_ReadsTheOwnersSearchSettings()
    {
        using JsonDocument json = JsonDocument.Parse("""{"search":{"useKeywordSearch":false,"linkWeight":0.4}}""");

        SearchSettings? own = SourceSearchChoices.Parse(json);

        own!.UseKeywordSearch.Should().BeFalse();
        own.LinkWeight.Should().Be(0.4f);
    }

    [Fact]
    public void Parse_OverrideSavedBeforeTheLinkLayer_IsNotApplied()
    {
        using JsonDocument json = JsonDocument.Parse("""{"search":{"fusionAlpha":0.1,"mode":"Keyword"}}""");

        SourceSearchChoices.Parse(json).Should().BeNull();
    }

    [Fact]
    public void Defaults_LinkSearchIsOff()
    {
        SearchSettings defaults = new();

        defaults.EnableLinkSearch.Should().BeFalse();
        defaults.UseVectorSearch.Should().BeTrue();
        defaults.UseKeywordSearch.Should().BeTrue();
    }
}

using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class CompositeSearchResultVerifierTests
{
    // Drops hits whose ResourceUri-style metadata starts with the given scheme, and records the
    // topK it was handed so the test can prove no inner verifier caps.
    private sealed class SchemeDroppingVerifier(string droppedScheme, int multiplier = 3) : IPerSchemeResultVerifier
    {
        public int? SeenTopK { get; private set; }
        public int CandidateMultiplier => multiplier;

        public Task<IReadOnlyList<SearchHit>> VerifyAsync(
            IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default)
        {
            SeenTopK = topK;
            IReadOnlyList<SearchHit> kept =
                [.. rankedCandidates.Where(h => !h.Metadata["uri"].StartsWith(droppedScheme, StringComparison.Ordinal))];
            return Task.FromResult(kept);
        }
    }

    private sealed class PassThroughVerifier : IPerSchemeResultVerifier
    {
        public int CandidateMultiplier => 1;

        public Task<IReadOnlyList<SearchHit>> VerifyAsync(
            IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default) =>
            Task.FromResult(rankedCandidates);
    }

    // Pass-through whose multiplier rises while it verifies, the way a verifier's first refresh
    // can learn that it enforces only after the search was already sized.
    private sealed class LearnsDuringVerifyVerifier : IPerSchemeResultVerifier
    {
        public int CandidateMultiplier { get; private set; } = 1;

        public Task<IReadOnlyList<SearchHit>> VerifyAsync(
            IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default)
        {
            CandidateMultiplier = 3;
            return Task.FromResult(rankedCandidates);
        }
    }

    private static SearchHit Hit(string id, string uri) =>
        new(id, $"doc-{id}", "content", 1f, new Dictionary<string, string> { ["uri"] = uri });

    [Fact]
    public async Task VerifyAsync_TwoVerifiers_EachDropsOnlyItsOwnHits()
    {
        var composite = new CompositeSearchResultVerifier(
            [new SchemeDroppingVerifier("s3://x"), new SchemeDroppingVerifier("atlassian://y")]);
        IReadOnlyList<SearchHit> candidates =
        [
            Hit("1", "s3://x/a"), Hit("2", "atlassian://y/b"), Hit("3", "azblob://z/c"), Hit("4", "s3://ok/d"),
        ];

        var result = await composite.VerifyAsync(candidates, Guid.NewGuid(), 10);

        result.Select(h => h.ChunkId).Should().Equal("3", "4");
    }

    [Fact]
    public async Task VerifyAsync_AnyEnforcing_CapsAtTopKAfterBackfill()
    {
        var first = new SchemeDroppingVerifier("s3://x");
        var second = new SchemeDroppingVerifier("atlassian://y");
        var composite = new CompositeSearchResultVerifier([first, second]);
        IReadOnlyList<SearchHit> candidates =
        [
            Hit("1", "s3://x/a"), Hit("2", "azblob://a"), Hit("3", "atlassian://y/b"),
            Hit("4", "azblob://b"), Hit("5", "azblob://c"), Hit("6", "azblob://d"),
        ];

        var result = await composite.VerifyAsync(candidates, null, 2);

        result.Select(h => h.ChunkId).Should().Equal("2", "4");
        first.SeenTopK.Should().Be(6, "no inner verifier may cap");
        second.SeenTopK.Should().Be(5,"the second sees the first's survivors, still uncapped");
    }

    [Fact]
    public async Task VerifyAsync_AllPassThrough_ReturnsUncappedPool()
    {
        var composite = new CompositeSearchResultVerifier([new PassThroughVerifier(), new PassThroughVerifier()]);
        IReadOnlyList<SearchHit> candidates = [Hit("1", "a://"), Hit("2", "b://"), Hit("3", "c://")];

        var result = await composite.VerifyAsync(candidates, null, 1);

        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task VerifyAsync_MultiplierRisesDuringVerify_CapsByTheValueTheSearchWasSizedWith()
    {
        var composite = new CompositeSearchResultVerifier([new LearnsDuringVerifyVerifier()]);
        IReadOnlyList<SearchHit> candidates = [Hit("1", "a://"), Hit("2", "b://"), Hit("3", "c://")];

        var result = await composite.VerifyAsync(candidates, null, 1);

        result.Should().HaveCount(3, "the search was sized at 1x, so the pool goes to AutoCut uncapped");
    }

    [Fact]
    public void CandidateMultiplier_IsMaxOfInner()
    {
        new CompositeSearchResultVerifier(
                [new SchemeDroppingVerifier("a", 2), new SchemeDroppingVerifier("b", 5), new PassThroughVerifier()])
            .CandidateMultiplier.Should().Be(5);
        new CompositeSearchResultVerifier([]).CandidateMultiplier.Should().Be(1);
    }
}

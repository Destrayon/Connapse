using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

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

        public async Task<IReadOnlyList<SearchHit>> VerifyAsync(
            IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default) =>
            (await VerifyScopedAsync(rankedCandidates, userId, topK, ct)).Hits;

        public Task<PerSchemeVerification> VerifyScopedAsync(
            IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default)
        {
            SeenTopK = topK;
            IReadOnlyList<SearchHit> kept =
                [.. rankedCandidates.Where(h => !h.Metadata["uri"].StartsWith(droppedScheme, StringComparison.Ordinal))];
            return Task.FromResult(new PerSchemeVerification(kept, Enforced: true));
        }
    }

    private sealed class PassThroughVerifier : IPerSchemeResultVerifier
    {
        public int CandidateMultiplier => 1;

        public Task<IReadOnlyList<SearchHit>> VerifyAsync(
            IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default) =>
            Task.FromResult(rankedCandidates);

        public Task<PerSchemeVerification> VerifyScopedAsync(
            IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default) =>
            Task.FromResult(new PerSchemeVerification(rankedCandidates, Enforced: false));
    }

    private static IOptionsMonitor<T> Opt<T>(T v) where T : class
    {
        var m = Substitute.For<IOptionsMonitor<T>>();
        m.CurrentValue.Returns(v);
        return m;
    }

    // A real Azure verifier whose pool is entirely non-cloud hits, so enforcement alone decides capping.
    private static AzureSearchResultVerifier RealAzure(bool configured, bool enforcing, int multiplier)
    {
        var docs = Substitute.For<IDocumentStore>();
        docs.GetResourceUrisAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<string>>().ToDictionary(d => d, _ => (string?)null));
        var azureAd = Opt(configured
            ? new AzureAdSignInSettings { TenantId = "t", ClientId = "c", RedirectUri = "https://x/cb", ClientCertificatePath = "p.pem" }
            : new AzureAdSignInSettings());
        return new AzureSearchResultVerifier(
            docs, Substitute.For<IAzureIdentityLinkReader>(), Substitute.For<IAzureDirectoryReader>(),
            Substitute.For<IAzureRbacReader>(), Substitute.For<IGen2FileAclReader>(), Substitute.For<IBlobTagReader>(),
            new AncestorTraverseResolver(Substitute.For<IGen2DirectoryReader>(), new MemoryCache(new MemoryCacheOptions())),
            azureAd, Opt(new PermissionEnforcementSettings { AzureEnforcing = enforcing }), EnforcementMigration.Completed(),
            Options.Create(new AzureVerifierSettings { MaxParallelism = 4, CandidateMultiplier = multiplier }),
            NullLogger<AzureSearchResultVerifier>.Instance);
    }

    private static readonly IReadOnlyList<SearchHit> FiveHits =
        [Hit("1", "x://a"), Hit("2", "x://b"), Hit("3", "x://c"), Hit("4", "x://d"), Hit("5", "x://e")];

    [Theory]
    [InlineData(true, true, 1, 3)]   // Enforcing with multiplier 1: Azure caps
    [InlineData(false, true, 5, 3)]  // EnforcingButUnusable (multiplier is 1): Azure caps
    [InlineData(true, false, 5, 5)]  // NotEnforcing: untouched pool for AutoCut
    public async Task VerifyAsync_RealAzure_MatchesStandaloneAzure(
        bool configured, bool enforcing, int multiplier, int expectedCount)
    {
        var standalone = await RealAzure(configured, enforcing, multiplier).VerifyAsync(FiveHits, Guid.NewGuid(), 3);
        var composed = await new CompositeSearchResultVerifier([RealAzure(configured, enforcing, multiplier)])
            .VerifyAsync(FiveHits, Guid.NewGuid(), 3);

        composed.Select(h => h.ChunkId).Should().Equal(standalone.Select(h => h.ChunkId));
        composed.Should().HaveCount(expectedCount);
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
    public void CandidateMultiplier_IsMaxOfInner()
    {
        new CompositeSearchResultVerifier(
                [new SchemeDroppingVerifier("a", 2), new SchemeDroppingVerifier("b", 5), new PassThroughVerifier()])
            .CandidateMultiplier.Should().Be(5);
        new CompositeSearchResultVerifier([]).CandidateMultiplier.Should().Be(1);
    }
}

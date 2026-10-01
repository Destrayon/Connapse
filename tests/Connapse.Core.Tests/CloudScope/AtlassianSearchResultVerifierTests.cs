using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static Connapse.Core.Tests.CloudScope.ConfluencePermissionCheckerTests;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public sealed class AtlassianSearchResultVerifierTests : IDisposable
{
    private const string Account = "557058:abc";
    private static readonly string PageRoot = $"/ex/confluence/{CloudId}/wiki/rest/api/content/";

    private readonly FakeAtlassianApi _api = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IAtlassianIdentityLinkReader _links = Substitute.For<IAtlassianIdentityLinkReader>();
    private readonly IConnectionStore _connections = AtlassianConnectionStore();
    private readonly Dictionary<string, string?> _uris = [];
    private readonly Guid _user = Guid.NewGuid();

    public AtlassianSearchResultVerifierTests()
    {
        _documents.GetResourceUrisAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<string, string?>)_uris);
        _links.GetLinkAsync(_user, Arg.Any<CancellationToken>()).Returns(new AtlassianIdentityRef(Account, "Ada"));
    }

    public void Dispose()
    {
        _api.Dispose();
        _cache.Dispose();
    }

    private AtlassianSearchResultVerifier NewVerifier(HttpMessageHandler? handler = null, AtlassianVerifierSettings? settings = null) =>
        new(_documents, _links, _connections, NewChecker(handler ?? _api, _cache, _connections), _cache,
            Options.Create(settings ?? new AtlassianVerifierSettings()), NullLogger<AtlassianSearchResultVerifier>.Instance);

    private SearchHit Hit(string id, string? uri)
    {
        _uris[id] = uri;
        return new SearchHit("c-" + id, id, "", 1f, []);
    }

    private void Allow(string contentId, bool allowed) =>
        _api.MapJson(PageRoot + contentId + "/permission/check", new { hasPermission = allowed });

    private static string[] Ids(IEnumerable<SearchHit> hits) => [.. hits.Select(h => h.DocumentId)];

    [Fact]
    public async Task Verify_NonAtlassianHits_PassUntouched()
    {
        SearchHit[] hits =
        [
            Hit("a", null), Hit("b", "s3://bucket/x"), Hit("c", "azblob://acct/c/x"), Hit("d", "github://1/x"),
        ];

        var result = await NewVerifier().VerifyAsync(hits, _user, 1);

        Ids(result).Should().Equal("a", "b", "c", "d"); // rank order kept, and never capped at topK
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyScoped_NothingDropped_IsNotEnforced()
    {
        Allow("1", true);
        SearchHit[] hits = [Hit("a", null), Hit("p1", AtlassianUri.ForPage(CloudId, "1"))];

        var result = await NewVerifier().VerifyScopedAsync(hits, _user, 1);

        result.Enforced.Should().BeFalse(); // pass-through, so the composite must not cap before AutoCut
        Ids(result.Hits).Should().Equal("a", "p1");
    }

    [Fact]
    public async Task VerifyScoped_ADeniedHit_IsEnforced()
    {
        Allow("1", false);
        SearchHit[] hits = [Hit("a", null), Hit("p1", AtlassianUri.ForPage(CloudId, "1"))];

        var result = await NewVerifier().VerifyScopedAsync(hits, _user, 1);

        result.Enforced.Should().BeTrue();
        Ids(result.Hits).Should().Equal("a");
    }

    [Fact]
    public async Task Verify_MixedHits_KeepsRankOrderAndDropsOnlyDenied()
    {
        Allow("1", true);
        Allow("2", false);
        Allow("3", true);
        SearchHit[] hits =
        [
            Hit("p1", AtlassianUri.ForPage(CloudId, "1")), Hit("s3", "s3://b/x"),
            Hit("p2", AtlassianUri.ForPage(CloudId, "2")), Hit("p3", AtlassianUri.ForPage(CloudId, "3")),
        ];

        var result = await NewVerifier().VerifyAsync(hits, _user, 2);

        Ids(result).Should().Equal("p1", "s3", "p3");
    }

    [Fact]
    public async Task Verify_UnlinkedUser_DropsAtlassianHits()
    {
        Allow("1", true);
        SearchHit[] hits = [Hit("p1", AtlassianUri.ForPage(CloudId, "1")), Hit("x", null)];

        (await NewVerifier().VerifyAsync(hits, Guid.NewGuid(), 10)).Should().ContainSingle().Which.DocumentId.Should().Be("x");
        (await NewVerifier().VerifyAsync(hits, null, 10)).Should().ContainSingle().Which.DocumentId.Should().Be("x");
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Verify_BudgetExceeded_DropsUnchecked()
    {
        Allow("1", true);
        Allow("2", true);
        // Token requests answer at once; every permission check hangs until it is cancelled.
        var slow = new StallingHandler(_api);
        SearchHit[] hits = [Hit("p1", AtlassianUri.ForPage(CloudId, "1")), Hit("x", null), Hit("p2", AtlassianUri.ForPage(CloudId, "2"))];

        var started = DateTime.UtcNow;
        var result = await NewVerifier(slow, new AtlassianVerifierSettings { BudgetMs = 200 }).VerifyAsync(hits, _user, 10);

        Ids(result).Should().Equal("x");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Verify_CallerCancelled_Throws()
    {
        Allow("1", true);
        var slow = new StallingHandler(_api);
        SearchHit[] hits = [Hit("p1", AtlassianUri.ForPage(CloudId, "1"))];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await FluentActions.Awaiting(() => NewVerifier(slow).VerifyAsync(hits, _user, 10, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Verify_AttachmentHit_ChecksPageId()
    {
        Allow("1", true);
        SearchHit[] hits = [Hit("att", AtlassianUri.ForAttachment(CloudId, "1", "900"))];

        var result = await NewVerifier().VerifyAsync(hits, _user, 10);

        Ids(result).Should().Equal("att");
        _api.Calls.Keys.Should().Contain(PageRoot + "1/permission/check")
            .And.NotContain(k => k.Contains("/900/"));
    }

    [Fact]
    public async Task Verify_MissingDocumentRow_Drops()
    {
        Allow("1", true);
        _uris.Clear();
        SearchHit[] hits = [new SearchHit("c", "gone", "", 1f, []), Hit("x", null)];

        Ids(await NewVerifier().VerifyAsync(hits, _user, 10)).Should().Equal("x");
    }

    [Theory]
    [InlineData("atlassian://not-a-guid/confluence/page/1")]
    [InlineData("atlassian://" + CloudId + "/confluence/page/abc")]
    [InlineData("atlassian://" + CloudId + "/confluence/page/1/extra")]
    public async Task Verify_UnparseableAtlassianUri_Drops(string uri)
    {
        Allow("1", true);
        SearchHit[] hits = [Hit("bad", uri)];

        (await NewVerifier().VerifyAsync(hits, _user, 10)).Should().BeEmpty();
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Verify_ManyChunksOfOnePage_ChecksEachPageOnce()
    {
        Allow("1", true);
        Allow("2", false);
        Hit("p1", AtlassianUri.ForPage(CloudId, "1"));
        List<SearchHit> hits = [.. Enumerable.Range(0, 10).Select(i => new SearchHit($"c{i}", "p1", "", 1f, []))];
        hits.Insert(3, Hit("att", AtlassianUri.ForAttachment(CloudId, "1", "900"))); // governed by page 1 too
        hits.Insert(5, Hit("p2", AtlassianUri.ForPage(CloudId, "2")));
        var slow = new DelayingHandler(_api, TimeSpan.FromMilliseconds(200));

        var result = await NewVerifier(slow).VerifyAsync(hits, _user, 10);

        result.Select(h => h.ChunkId).Should().Equal(hits.Where(h => h.DocumentId != "p2").Select(h => h.ChunkId));
        _api.Calls.Where(c => c.Key.EndsWith("/permission/check")).Sum(c => c.Value).Should().Be(2);
    }

    [Fact]
    public async Task Verify_ManyChunksOfOnePage_DistinctPagesStillFinishWithinBudget()
    {
        foreach (string id in new[] { "1", "2", "3", "4" })
            Allow(id, true);
        Hit("p1", AtlassianUri.ForPage(CloudId, "1"));
        List<SearchHit> hits = [.. Enumerable.Range(0, 10).Select(i => new SearchHit($"c{i}", "p1", "", 1f, []))];
        hits.AddRange([Hit("p2", AtlassianUri.ForPage(CloudId, "2")), Hit("p3", AtlassianUri.ForPage(CloudId, "3")),
            Hit("p4", AtlassianUri.ForPage(CloudId, "4"))]);
        // Every check takes two seconds against the default three-second budget and ten slots.
        var slow = new DelayingHandler(_api, TimeSpan.FromSeconds(2));

        var result = await NewVerifier(slow).VerifyAsync(hits, _user, 10);

        result.Select(h => h.ChunkId).Should().Equal(hits.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task Verify_ManyUncachedChecksOnOneSite_LookUpTheConnectionOnce()
    {
        var checkerConnections = AtlassianConnectionStore();
        var verifier = new AtlassianSearchResultVerifier(_documents, _links, _connections,
            NewChecker(_api, _cache, checkerConnections), _cache,
            Options.Create(new AtlassianVerifierSettings()), NullLogger<AtlassianSearchResultVerifier>.Instance);
        SearchHit[] hits = [.. Enumerable.Range(1, 8).Select(i =>
        {
            Allow(i.ToString(), true);
            return Hit("p" + i, AtlassianUri.ForPage(CloudId, i.ToString()));
        })];

        (await verifier.VerifyAsync(hits, _user, 10)).Should().HaveCount(8);

        await checkerConnections.Received(1).ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await checkerConnections.Received(1).GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CandidateMultiplier_AtlassianConnectionExists_IsConfigured()
    {
        var verifier = NewVerifier(settings: new AtlassianVerifierSettings { CandidateMultiplier = 4 });
        await verifier.VerifyAsync([], _user, 10);

        verifier.CandidateMultiplier.Should().Be(4);
    }

    [Fact]
    public async Task CandidateMultiplier_NoAtlassianConnection_IsOne()
    {
        _connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        var verifier = NewVerifier();
        await verifier.VerifyAsync([], _user, 10);

        verifier.CandidateMultiplier.Should().Be(1);
    }

    /// <summary>Passes token requests through; holds every other request until it is cancelled.</summary>
    private sealed class StallingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "auth.atlassian.com")
                await Task.Delay(Timeout.Infinite, ct);
            return await base.SendAsync(request, ct);
        }
    }

    /// <summary>Passes token requests through; answers every other request after a fixed delay.</summary>
    private sealed class DelayingHandler(HttpMessageHandler inner, TimeSpan delay) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "auth.atlassian.com")
                await Task.Delay(delay, ct);
            return await base.SendAsync(request, ct);
        }
    }
}

using System.Net;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Core.Utilities;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.CloudScope;

/// <summary>
/// Connected custom sources at search time: the resolver turns the keys a user holds into a grant, and the
/// verifier asks the middle man about every <c>custom://</c> hit, denying anything it cannot confirm.
/// </summary>
[Trait("Category", "Unit")]
public class CustomPermissionTests
{
    private static readonly Guid Alice = Guid.NewGuid();

    private readonly FakeMiddleMan _remote = new();
    private readonly IConnectionStore _connections = Substitute.For<IConnectionStore>();
    private readonly ISourceStore _sources = Substitute.For<ISourceStore>();
    private readonly ICustomProviderStore _providers = Substitute.For<ICustomProviderStore>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly CustomConnectionPresence _presence = new();
    private readonly Dictionary<string, string?> _uris = [];

    private CustomProvider _provider;
    private readonly Connection _connection;
    private readonly Source _source;

    public CustomPermissionTests()
    {
        _provider = new CustomProvider(Guid.NewGuid(), "Acme", FakeMiddleMan.BaseUrl, true, 100, true, null,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);
        _connection = new Connection(Guid.NewGuid(), "acme", ConnectionProvider.Custom,
            CustomConnectionConfig.ToJson(_provider.Id), null, DateTime.UtcNow, DateTime.UtcNow);
        _source = new Source(Guid.NewGuid(), "eng", null, _connection.Id,
            new CustomSourceScope(FakeMiddleMan.BaseUrl, FakeMiddleMan.Scope).ToJson(), DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);

        _providers.GetAsync(_provider.Id, Arg.Any<CancellationToken>()).Returns(_ => _provider);
        _providers.GetTokenAsync(_provider.Id, Arg.Any<CancellationToken>()).Returns(FakeMiddleMan.Token);
        _connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Connection>>([_connection]));
        _connections.GetAsync(_connection.Id, Arg.Any<CancellationToken>()).Returns(_connection);
        _sources.ListByConnectionAsync(_connection.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Source>>([_source]));
        _sources.GetAsync(_source.Id, Arg.Any<CancellationToken>()).Returns(_source);
        _documents.GetResourceUrisAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<string, string?>)call.Arg<IReadOnlyCollection<string>>()
                .Where(_uris.ContainsKey).ToDictionary(id => id, id => _uris[id]));

        _remote.Put("a", "README.md", "a");
        _remote.Put("b", "design/b.md", "b");
        _remote.Put("c", "secret/c.md", "c");
        _remote.Grants[Alice.ToString()] = ["folder:", "folder:design"];
        _remote.Denies[Alice.ToString()] = ["b"];
    }

    private IHttpClientFactory Http()
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_remote, disposeHandler: false));
        return http;
    }

    private static IOptionsMonitor<CustomSourceSettings> Settings()
    {
        var settings = Substitute.For<IOptionsMonitor<CustomSourceSettings>>();
        settings.CurrentValue.Returns(new CustomSourceSettings());
        return settings;
    }

    private CustomSearchScopeResolver Resolver() => new(
        _connections, _sources, _providers, Http(), Settings(), _presence, NullLogger<CustomSearchScopeResolver>.Instance);

    private CustomSearchResultVerifier Verifier() => new(
        _documents, _sources, _connections, _providers, Http(), Settings(), _presence, NullLogger<CustomSearchResultVerifier>.Instance);

    private static string Prefix(Guid sourceId) => CustomResourceUri.SourcePrefix(sourceId);

    // ── Resolver ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_NoUser_IsNoPrincipal()
    {
        (await Resolver().ResolveAsync(null)).Outcome.Should().Be(ScopeOutcome.NoPrincipal);
    }

    [Fact]
    public async Task Resolve_UserHoldingKeys_GrantsTheSourcePrefixWithThoseKeys()
    {
        SearchScopes scopes = await Resolver().ResolveAsync(Alice);

        GrantMatch match = scopes.Matches.Should().ContainSingle().Subject;
        match.Value.Should().Be(Prefix(_source.Id));
        match.AccessKeys.Should().Equal("folder:", "folder:design");
        _presence.Known.Should().BeTrue();
    }

    [Fact]
    public async Task Resolve_UserHoldingNoKeys_IsGrantedNothing()
    {
        SearchScopes scopes = await Resolver().ResolveAsync(Guid.NewGuid());

        scopes.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_ProviderWithoutKeys_GrantsTheBarePrefixForTheCheckToDecide()
    {
        _provider = _provider with { SupportsAccessKeys = false };

        GrantMatch match = (await Resolver().ResolveAsync(Alice)).Matches.Should().ContainSingle().Subject;

        match.AccessKeys.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_KeysCallFails_GrantsTheBarePrefixForTheCheckToDecide()
    {
        _remote.FailNextWith = HttpStatusCode.InternalServerError;

        GrantMatch match = (await Resolver().ResolveAsync(Alice)).Matches.Should().ContainSingle().Subject;

        match.AccessKeys.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_MoreKeysThanConnapseReads_GrantsTheBarePrefixForTheCheckToDecide()
    {
        _remote.Grants[Alice.ToString()] = Enumerable.Range(0, CustomSearchScopeResolver.MaxKeys + 1).Select(i => $"k{i}").ToList();

        GrantMatch match = (await Resolver().ResolveAsync(Alice)).Matches.Should().ContainSingle().Subject;

        match.AccessKeys.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_ConnectionWhoseProviderIsGone_GrantsNothing()
    {
        _providers.GetAsync(_provider.Id, Arg.Any<CancellationToken>()).Returns((CustomProvider?)null);

        (await Resolver().ResolveAsync(Alice)).Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_NoCustomConnections_GrantsNothingAndRecordsIt()
    {
        _connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Connection>>([]));

        (await Resolver().ResolveAsync(Alice)).Matches.Should().BeEmpty();
        _presence.Known.Should().BeFalse();
    }

    // ── Verifier ──────────────────────────────────────────────────────────

    private static SearchHit Hit(string documentId) => new("", documentId, "", 1f, []);

    private List<SearchHit> Hits()
    {
        _uris["doc-a"] = CustomResourceUri.For(_source.Id, "a");
        _uris["doc-b"] = CustomResourceUri.For(_source.Id, "b");
        _uris["doc-c"] = CustomResourceUri.For(_source.Id, "c");
        _uris["doc-s3"] = "s3://bucket/key";
        _uris["doc-upload"] = null;
        return [Hit("doc-a"), Hit("doc-b"), Hit("doc-s3"), Hit("doc-c"), Hit("doc-upload"), Hit("doc-missing")];
    }

    private static IEnumerable<string> Ids(PerSchemeVerification result) => result.Hits.Select(h => h.DocumentId);

    [Fact]
    public async Task Verify_KeepsOnlyCustomHitsTheMiddleManAllows_InRankOrder()
    {
        PerSchemeVerification result = await Verifier().VerifyScopedAsync(Hits(), Alice, 10);

        Ids(result).Should().Equal("doc-a", "doc-s3", "doc-upload");
        result.Enforced.Should().BeTrue();
    }

    [Fact]
    public async Task Verify_ErrorAlongsideTrue_IsDenied()
    {
        _remote.Quirk = EvaluationQuirk.ErrorAlongsideTrue;

        Ids(await Verifier().VerifyScopedAsync(Hits(), Alice, 10)).Should().NotContain("doc-a");
    }

    [Theory]
    [InlineData(EvaluationQuirk.ServerError)]
    [InlineData(EvaluationQuirk.WrongCount)]
    public async Task Verify_AnswerThatCannotBeTrusted_DeniesEveryCustomHit(EvaluationQuirk quirk)
    {
        _remote.Quirk = quirk;

        Ids(await Verifier().VerifyScopedAsync(Hits(), Alice, 10)).Should().Equal("doc-s3", "doc-upload");
    }

    [Fact]
    public async Task Verify_NoUser_DeniesEveryCustomHitWithoutAsking()
    {
        Ids(await Verifier().VerifyScopedAsync(Hits(), null, 10)).Should().Equal("doc-s3", "doc-upload");
        _remote.EvaluationCalls.Should().Be(0);
    }

    [Fact]
    public async Task Verify_SourceNoLongerOnACustomConnection_DeniesItsHits()
    {
        _sources.GetAsync(_source.Id, Arg.Any<CancellationToken>()).Returns(_source with { ConnectionId = null, Provider = ConnectionProvider.Custom });

        Ids(await Verifier().VerifyScopedAsync(Hits(), Alice, 10)).Should().NotContain(["doc-a", "doc-b", "doc-c"]);
    }

    [Fact]
    public async Task Verify_UnparseableCustomUri_IsDenied()
    {
        _uris["doc-bad"] = "custom://not-a-guid/x";

        Ids(await Verifier().VerifyScopedAsync([Hit("doc-bad")], Alice, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Verify_SplitsChecksIntoTheProvidersBatchSize()
    {
        _provider = _provider with { MaxCheckBatch = 1 };
        _remote.MaxCheckBatch = 1;

        Ids(await Verifier().VerifyScopedAsync(Hits(), Alice, 10)).Should().Contain("doc-a");
        _remote.EvaluationCalls.Should().Be(3);
    }

    [Fact]
    public void CandidateMultiplier_OverFetchesUnlessCustomConnectionsAreKnownAbsent()
    {
        Verifier().CandidateMultiplier.Should().Be(3);
        _presence.Record(false);
        Verifier().CandidateMultiplier.Should().Be(1);
        _presence.Record(true);
        Verifier().CandidateMultiplier.Should().Be(3);
    }

    [Fact]
    public async Task Verify_SourceReadingADifferentMiddleManThanItsProvider_IsDenied()
    {
        _provider = _provider with { BaseUrl = "https://elsewhere.acme.test" };

        Ids(await Verifier().VerifyScopedAsync(Hits(), Alice, 10)).Should().NotContain(["doc-a", "doc-b", "doc-c"]);
    }

    [Fact]
    public async Task Resolve_SourceReadingADifferentMiddleManThanItsProvider_GrantsNothing()
    {
        _provider = _provider with { BaseUrl = "https://elsewhere.acme.test" };

        (await Resolver().ResolveAsync(Alice)).Matches.Should().BeEmpty();
    }
}

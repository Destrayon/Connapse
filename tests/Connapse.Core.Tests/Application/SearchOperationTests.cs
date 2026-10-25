using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Search;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using CsCheck;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

[Trait("Category", "Unit")]
public class SearchOperationTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly OperationContext Caller = new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "test")), OperationSurface.Rest);

    /// <summary>The stores, ports and search one test or one sample calls.</summary>
    private sealed class World
    {
        public readonly IContainerStore Containers = Substitute.For<IContainerStore>();
        public readonly ISourceStore Sources = Substitute.For<ISourceStore>();
        public readonly ISourceVisibility Visibility = Substitute.For<ISourceVisibility>();
        public readonly IKnowledgeSearch Search = Substitute.For<IKnowledgeSearch>();
        public readonly IOptionsMonitor<SearchSettings> Settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        public readonly IOperationAuthorizer Authorizer = Substitute.For<IOperationAuthorizer>();
        public SearchOptions? Asked;
        public string? AskedQuery;

        public World(bool allowed = true, int hits = 1, double minimumScore = 0.25)
        {
            Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(false);
            Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.KnowledgeRead).Returns(allowed);
            Visibility.ForAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>()).Returns(_ => true);
            Settings.CurrentValue.Returns(new SearchSettings { MinimumScore = minimumScore });
            var found = Enumerable.Range(0, hits).Select(i => new SearchHit($"c{i}", $"d{i}", "text", 0.5f, [])).ToList();
            Search.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>()).Returns(c =>
            {
                AskedQuery = c.ArgAt<string>(0);
                Asked = c.ArgAt<SearchOptions>(1);
                return new SearchResult(found, found.Count, TimeSpan.Zero);
            });
        }

        public SearchKnowledge Operation() => new(Containers, Sources, Visibility, Search, Settings, Authorizer);

        public Container Container(string name = "docs")
        {
            var container = new Container(Guid.NewGuid().ToString(), name, null, Epoch, Epoch);
            Containers.GetAsync(Guid.Parse(container.Id), Arg.Any<CancellationToken>()).Returns(container);
            Containers.GetByNameAsync(name, Arg.Any<CancellationToken>()).Returns(container);
            return container;
        }

        public Source Source(string name = "wiki", bool visible = true, bool searchableWhenHidden = false)
        {
            var source = new Source(Guid.NewGuid(), name, null, null, "{}", Epoch, Epoch);
            Sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);
            Sources.GetByNameAsync(name, Arg.Any<CancellationToken>()).Returns(source);
            if (!visible)
                Visibility.ForAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>()).Returns(_ => s => s.Id != source.Id);
            Visibility.IsSearchableWhenHidden(source).Returns(searchableWhenHidden);
            return source;
        }

        public bool Searched => Search.ReceivedCalls().Any();

        public bool Looked => Containers.ReceivedCalls().Any() || Sources.ReceivedCalls().Any();
    }

    private static SearchRequest Query(string? query = "find me") => new(query);

    // ── who may search ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_CallerWithoutKnowledgeRead_IsForbiddenAndLooksNothingUp()
    {
        var world = new World(allowed: false);
        var container = world.Container();

        var searched = await world.Operation().RunAsync(Caller, new SearchScope(container.Id), Query());

        searched.Status.Should().Be(SearchStatus.Forbidden);
        searched.Detail.Should().Be("You don't have permission to search.");
        world.Looked.Should().BeFalse();
        world.Searched.Should().BeFalse();
    }

    // ── the request ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "query is required", "query")]
    [InlineData("", "query is required", "query")]
    [InlineData("   ", "query is required", "query")]
    public async Task Search_NoQuery_IsInvalid(string? query, string detail, string field)
    {
        var world = new World();
        var container = world.Container();

        var searched = await world.Operation().RunAsync(Caller, new SearchScope(container.Id), Query(query));

        searched.Status.Should().Be(SearchStatus.Invalid);
        searched.Detail.Should().Be(detail);
        searched.Field.Should().Be(field);
        world.Looked.Should().BeFalse("a bad request is answered before the scope is looked up");
    }

    [Theory]
    [InlineData(10_000, true)]
    [InlineData(10_001, false)]
    public async Task Search_QueryLength_IsCappedAtTheMaximum(int length, bool allowed)
    {
        var world = new World();
        var container = world.Container();

        var searched = await world.Operation().RunAsync(Caller, new SearchScope(container.Id), Query(new string('q', length)));

        searched.Status.Should().Be(allowed ? SearchStatus.Success : SearchStatus.Invalid);
        if (!allowed)
        {
            searched.Detail.Should().Be("query must not exceed 10000 characters");
            searched.Field.Should().Be("query");
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    public async Task Search_TopK_IsBetweenOneAndAHundred(int topK, bool allowed)
    {
        var world = new World();
        var container = world.Container();

        var searched = await world.Operation().RunAsync(Caller, new SearchScope(container.Id), Query() with { TopK = topK });

        searched.Status.Should().Be(allowed ? SearchStatus.Success : SearchStatus.Invalid);
        if (allowed)
            world.Asked!.TopK.Should().Be(topK);
        else
        {
            searched.Detail.Should().Be("topK must be between 1 and 100");
            searched.Field.Should().Be("topK");
        }
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(1f, true)]
    [InlineData(-0.01f, false)]
    [InlineData(1.01f, false)]
    [InlineData(float.NaN, false)]
    public async Task Search_MinScore_IsBetweenZeroAndOne(float minScore, bool allowed)
    {
        var world = new World();
        var container = world.Container();

        var searched = await world.Operation().RunAsync(Caller, new SearchScope(container.Id), Query() with { MinScore = minScore });

        searched.Status.Should().Be(allowed ? SearchStatus.Success : SearchStatus.Invalid);
        if (allowed)
            world.Asked!.MinScore.Should().Be(minScore);
        else
        {
            searched.Detail.Should().Be("minScore must be between 0.0 and 1.0");
            searched.Field.Should().Be("minScore");
        }
    }

    [Fact]
    public async Task Search_NothingButAQuery_UsesTheDefaults()
    {
        var world = new World(minimumScore: 0.25);
        var container = world.Container();

        var searched = await world.Operation().RunAsync(Caller, new SearchScope(container.Id), Query());

        searched.Status.Should().Be(SearchStatus.Success);
        world.AskedQuery.Should().Be("find me");
        world.Asked.Should().Be(new SearchOptions(TopK: 10, MinScore: 0.25f, ContainerId: container.Id, Mode: SearchMode.Hybrid, Filters: null, UserId: UserId));
    }

    [Fact]
    public async Task Search_EveryOption_ReachesTheSearch()
    {
        var world = new World();
        var container = world.Container();

        await world.Operation().RunAsync(Caller, new SearchScope(container.Id),
            new SearchRequest("q", SearchMode.Keyword, TopK: 3, MinScore: 0.5f, Path: "/docs/", Filters: new Dictionary<string, string> { ["tag"] = "a" }));

        world.Asked!.Mode.Should().Be(SearchMode.Keyword);
        world.Asked.TopK.Should().Be(3);
        world.Asked.MinScore.Should().Be(0.5f);
        world.Asked.Filters.Should().BeEquivalentTo(new Dictionary<string, string> { ["tag"] = "a", ["pathPrefix"] = "/docs/" });
    }

    [Fact]
    public async Task Search_PathAndAPathFilter_KeepsTheFilter()
    {
        var world = new World();
        var container = world.Container();

        await world.Operation().RunAsync(Caller, new SearchScope(container.Id),
            Query() with { Path = "/path/", Filters = new Dictionary<string, string> { ["pathPrefix"] = "/filter/" } });

        world.Asked!.Filters.Should().BeEquivalentTo(new Dictionary<string, string> { ["pathPrefix"] = "/filter/" });
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Search_BlankPath_FiltersNothing(string path)
    {
        var world = new World();
        var container = world.Container();

        await world.Operation().RunAsync(Caller, new SearchScope(container.Id), Query() with { Path = path, Filters = new Dictionary<string, string>() });

        world.Asked!.Filters.Should().BeNull();
    }

    // ── the scope ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ContainerById_SearchesItWithoutAskingAboutSources()
    {
        var world = new World();
        var container = world.Container();

        var searched = await world.Operation().RunAsync(Caller, new SearchScope(container.Id), Query());

        searched.Status.Should().Be(SearchStatus.Success);
        searched.Result!.Hits.Should().HaveCount(1);
        world.Asked!.ContainerId.Should().Be(container.Id);
        world.Sources.ReceivedCalls().Should().BeEmpty();
        world.Visibility.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Search_ContainerByName_MatchesItInAnyCase()
    {
        var world = new World();
        var container = world.Container("docs");

        var searched = await world.Operation().RunAsync(Caller, new SearchScope("DoCs"), Query());

        searched.Status.Should().Be(SearchStatus.Success);
        world.Asked!.ContainerId.Should().Be(container.Id);
    }

    [Fact]
    public async Task Search_ContainerWhoseIdIsNotAGuid_ReadsAsMissing()
    {
        var world = new World();
        world.Containers.GetByNameAsync("broken", Arg.Any<CancellationToken>()).Returns(new Container("not-a-guid", "broken", null, Epoch, Epoch));

        var searched = await world.Operation().RunAsync(Caller, new SearchScope("broken"), Query());

        searched.Status.Should().Be(SearchStatus.NotFound);
        world.Searched.Should().BeFalse();
    }

    [Fact]
    public async Task Search_SourceById_SearchesIt()
    {
        var world = new World();
        var source = world.Source();

        var searched = await world.Operation().RunAsync(Caller, new SearchScope(source.Id.ToString()), Query());

        searched.Status.Should().Be(SearchStatus.Success);
        world.Asked!.ContainerId.Should().Be(source.Id.ToString());
    }

    [Fact]
    public async Task Search_SourceByName_MatchesItInAnyCase()
    {
        var world = new World();
        var source = world.Source("wiki");

        var searched = await world.Operation().RunAsync(Caller, new SearchScope("WIKI"), Query());

        searched.Status.Should().Be(SearchStatus.Success);
        world.Asked!.ContainerId.Should().Be(source.Id.ToString());
    }

    [Fact]
    public async Task Search_HiddenSourceByName_ReadsAsMissingEvenIfSearchableById()
    {
        var world = new World();
        world.Source("space", visible: false, searchableWhenHidden: true);

        var searched = await world.Operation().RunAsync(Caller, new SearchScope("space"), Query());

        searched.Status.Should().Be(SearchStatus.NotFound);
        searched.Detail.Should().Be("Container or source 'space' not found");
        world.Searched.Should().BeFalse();
    }

    [Fact]
    public async Task Search_HiddenSourceNotSearchableWhenHidden_ReadsAsMissing()
    {
        var world = new World();
        var source = world.Source(visible: false, searchableWhenHidden: false);

        var searched = await world.Operation().RunAsync(Caller, new SearchScope(source.Id.ToString()), Query());

        searched.Status.Should().Be(SearchStatus.NotFound);
        world.Searched.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, SearchStatus.Success)]
    [InlineData(0, SearchStatus.NotFound)]
    public async Task Search_HiddenSourceSearchableById_IsMissingOnlyWhenNothingInItIsReadable(int hits, SearchStatus expected)
    {
        var world = new World(hits: hits);
        var source = world.Source(visible: false, searchableWhenHidden: true);

        var searched = await world.Operation().RunAsync(Caller, new SearchScope(source.Id.ToString()), Query());

        searched.Status.Should().Be(expected);
        world.Searched.Should().BeTrue();
    }

    [Fact]
    public async Task Search_VisibleScopeWithNoHits_SucceedsEmpty()
    {
        var world = new World(hits: 0);
        var source = world.Source();

        var searched = await world.Operation().RunAsync(Caller, new SearchScope(source.Id.ToString()), Query());

        searched.Status.Should().Be(SearchStatus.Success);
        searched.Result!.Hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_UnknownId_ReadsAsMissing()
    {
        var world = new World();
        var unknown = Guid.NewGuid();

        var searched = await world.Operation().RunAsync(Caller, new SearchScope(unknown.ToString()), Query());

        searched.Status.Should().Be(SearchStatus.NotFound);
        searched.Detail.Should().Be($"Container or source '{unknown}' not found");
    }

    [Fact]
    public async Task SearchContainer_SourceId_ReadsAsMissingWithoutAskingAboutSources()
    {
        var world = new World();
        var source = world.Source();

        var searched = await new SearchContainer(world.Operation()).RunAsync(Caller, source.Id, Query());

        searched.Status.Should().Be(SearchStatus.NotFound);
        searched.Detail.Should().Be($"Container {source.Id} not found");
        world.Sources.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SearchSource_ContainerId_ReadsAsMissingWithoutAskingAboutContainers()
    {
        var world = new World();
        var container = world.Container();

        var searched = await new SearchSource(world.Operation()).RunAsync(Caller, Guid.Parse(container.Id), Query());

        searched.Status.Should().Be(SearchStatus.NotFound);
        searched.Detail.Should().Be($"Source {container.Id} not found");
        world.Containers.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SearchContainer_ContainerId_SearchesIt()
    {
        var world = new World();
        var container = world.Container();

        var searched = await new SearchContainer(world.Operation()).RunAsync(Caller, Guid.Parse(container.Id), Query());

        searched.Status.Should().Be(SearchStatus.Success);
        world.Asked!.ContainerId.Should().Be(container.Id);
    }

    [Fact]
    public async Task SearchSource_SourceId_SearchesIt()
    {
        var world = new World();
        var source = world.Source();

        var searched = await new SearchSource(world.Operation()).RunAsync(Caller, source.Id, Query());

        searched.Status.Should().Be(SearchStatus.Success);
        world.Asked!.ContainerId.Should().Be(source.Id.ToString());
    }

    // ── random inputs ──────────────────────────────────────────────────────────────────────────

    /// <summary>What a sample names: a container or source by id or by name, in any case, or something that isn't there.</summary>
    private enum Named { ContainerId, SourceId, UnknownId, ContainerName, SourceName, UnknownName }

    private static readonly Gen<string?> AnyQuery = Gen.OneOf(
        Gen.Const((string?)null), Gen.Const((string?)""), Gen.Const((string?)" \t"), Gen.Const((string?)new string('q', 10_000)),
        Gen.Const((string?)new string('q', 10_001)), Gen.String[Gen.Char[" ab"], 0, 4].Select(s => (string?)s));

    private static readonly Gen<float?> AnyMinScore = Gen.OneOf(
        Gen.Const((float?)null), Gen.Const((float?)float.NaN), Gen.Const((float?)0f), Gen.Const((float?)1f), Gen.Float[-0.5f, 1.5f].Select(f => (float?)f));

    private static readonly Gen<IReadOnlyDictionary<string, string>?> AnyFilters = Gen.OneOf(
        Gen.Const((IReadOnlyDictionary<string, string>?)null),
        Gen.Const((IReadOnlyDictionary<string, string>?)new Dictionary<string, string>()),
        Gen.Const((IReadOnlyDictionary<string, string>?)new Dictionary<string, string> { ["pathPrefix"] = "/f/" }),
        Gen.Const((IReadOnlyDictionary<string, string>?)new Dictionary<string, string> { ["tag"] = "x" }));

    private static readonly Gen<SearchRequest> AnyRequest = Gen.Select(
        AnyQuery, Gen.Enum<SearchMode>(), Gen.Int[-1, 102].Nullable(), AnyMinScore,
        Gen.OneOf(Gen.Const((string?)null), Gen.Const((string?)""), Gen.Const((string?)"/docs/")), AnyFilters,
        (query, mode, topK, minScore, path, filters) => new SearchRequest(query, mode, topK, minScore, path, filters));

    private static readonly Gen<OperationContext> AnyCaller = Gen.Select(Gen.Enum<OperationSurface>(), Gen.Guid.Nullable(),
        (surface, user) => new OperationContext(new ClaimsPrincipal(user is Guid id
            ? new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test")
            : new ClaimsIdentity()), surface));

    /// <summary>What the request is refused for, by the rules written out again; null when it isn't.</summary>
    private static bool Refused(SearchRequest r) =>
        string.IsNullOrWhiteSpace(r.Query) || r.Query.Length > 10_000
        || r.TopK is < 1 or > 100
        || r.MinScore is { } s && (float.IsNaN(s) || s < 0f || s > 1f);

    [Fact]
    [RandomInputTestOf(typeof(SearchKnowledge))]
    public Task Search_AnyScopeAndRequest_SearchesExactlyWhatTheCallerMayRead() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Enum<SearchScopeKind>(), Gen.Enum<Named>(), Gen.Bool, Gen.Bool, Gen.Bool,
                Gen.Select(Gen.Bool, Gen.Int[0, 2], AnyRequest, Gen.Double[0, 0.5]))
            .SampleAsync(async t =>
            {
                var (caller, allowed, kind, named, exists, visible, searchableWhenHidden, (containerIdParses, hits, request, minimum)) = t;
                var world = new World(allowed, hits, minimum);
                var container = new Container(containerIdParses ? Guid.NewGuid().ToString() : "not-a-guid", "docs", null, Epoch, Epoch);
                var source = new Source(Guid.NewGuid(), "wiki", null, null, "{}", Epoch, Epoch);
                if (exists)
                {
                    if (Guid.TryParse(container.Id, out var containerGuid))
                        world.Containers.GetAsync(containerGuid, Arg.Any<CancellationToken>()).Returns(container);
                    world.Containers.GetByNameAsync("docs", Arg.Any<CancellationToken>()).Returns(container);
                    world.Sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);
                    world.Sources.GetByNameAsync("wiki", Arg.Any<CancellationToken>()).Returns(source);
                }
                world.Visibility.ForAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>()).Returns(_ => _ => visible);
                world.Visibility.IsSearchableWhenHidden(source).Returns(searchableWhenHidden);
                string target = named switch
                {
                    Named.ContainerId => containerIdParses ? container.Id : Guid.NewGuid().ToString(),
                    Named.SourceId => source.Id.ToString(),
                    Named.UnknownId => Guid.NewGuid().ToString(),
                    Named.ContainerName => "DOCS",
                    Named.SourceName => "Wiki",
                    _ => "nothing",
                };

                var operation = new SearchKnowledge(world.Containers, world.Sources, world.Visibility, world.Search, world.Settings, world.Authorizer);
                var searched = await operation.RunAsync(caller, new SearchScope(target, kind), request);

                if (!allowed)
                {
                    searched.Status.Should().Be(SearchStatus.Forbidden);
                    world.Looked.Should().BeFalse();
                    world.Searched.Should().BeFalse();
                    return;
                }
                if (Refused(request))
                {
                    searched.Status.Should().Be(SearchStatus.Invalid);
                    world.Looked.Should().BeFalse();
                    world.Searched.Should().BeFalse();
                    return;
                }

                bool containers = kind != SearchScopeKind.Source, sources = kind != SearchScopeKind.Container;
                (string Id, bool Discoverable)? owner = (named, exists) switch
                {
                    (Named.ContainerId, true) when containers && containerIdParses => (container.Id, true),
                    (Named.ContainerName, true) when containers => containerIdParses ? (container.Id, true) : null,
                    (Named.SourceId, true) when sources => visible ? (source.Id.ToString(), true) : searchableWhenHidden ? (source.Id.ToString(), false) : null,
                    (Named.SourceName, true) when sources && visible => (source.Id.ToString(), true),
                    _ => null,
                };
                if (owner is not var (ownerId, discoverable))
                {
                    searched.Status.Should().Be(SearchStatus.NotFound);
                    world.Searched.Should().BeFalse();
                    return;
                }

                var filters = request.Filters is null ? new Dictionary<string, string>() : new Dictionary<string, string>(request.Filters);
                if (!string.IsNullOrWhiteSpace(request.Path) && !filters.ContainsKey("pathPrefix"))
                    filters["pathPrefix"] = request.Path;
                world.AskedQuery.Should().Be(request.Query);
                world.Asked!.ContainerId.Should().Be(ownerId);
                world.Asked.TopK.Should().Be(request.TopK ?? 10);
                world.Asked.MinScore.Should().Be(request.MinScore ?? (float)minimum);
                world.Asked.Mode.Should().Be(request.Mode);
                world.Asked.UserId.Should().Be(SearchPrincipal.Resolve(caller.User));
                if (filters.Count == 0)
                    world.Asked.Filters.Should().BeNull();
                else
                    world.Asked.Filters.Should().BeEquivalentTo(filters);
                searched.Status.Should().Be(hits == 0 && !discoverable ? SearchStatus.NotFound : SearchStatus.Success);
            }, iter: 2000);

    [Fact]
    [RandomInputTestOf(typeof(SearchContainer))]
    public Task SearchContainer_AnyId_SearchesOnlyAContainer() =>
        Gen.Select(Gen.Bool, Gen.Bool, AnyRequest).SampleAsync(async t =>
        {
            var (isContainer, exists, request) = t;
            var world = new World();
            var id = Guid.NewGuid();
            if (exists && isContainer)
                world.Containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new Container(id.ToString(), "docs", null, Epoch, Epoch));
            if (exists && !isContainer)
                world.Sources.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new Source(id, "wiki", null, null, "{}", Epoch, Epoch));

            var searched = await new SearchContainer(world.Operation()).RunAsync(Caller, id, request);

            world.Sources.ReceivedCalls().Should().BeEmpty();
            searched.Status.Should().Be(Refused(request) ? SearchStatus.Invalid
                : exists && isContainer ? SearchStatus.Success : SearchStatus.NotFound);
        }, iter: 500);

    [Fact]
    [RandomInputTestOf(typeof(SearchSource))]
    public Task SearchSource_AnyId_SearchesOnlyASource() =>
        Gen.Select(Gen.Bool, Gen.Bool, AnyRequest).SampleAsync(async t =>
        {
            var (isSource, exists, request) = t;
            var world = new World();
            var id = Guid.NewGuid();
            if (exists && !isSource)
                world.Containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new Container(id.ToString(), "docs", null, Epoch, Epoch));
            if (exists && isSource)
                world.Sources.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new Source(id, "wiki", null, null, "{}", Epoch, Epoch));

            var searched = await new SearchSource(world.Operation()).RunAsync(Caller, id, request);

            world.Containers.ReceivedCalls().Should().BeEmpty();
            searched.Status.Should().Be(Refused(request) ? SearchStatus.Invalid
                : exists && isSource ? SearchStatus.Success : SearchStatus.NotFound);
        }, iter: 500);
}

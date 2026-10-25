using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Options;

namespace Connapse.Application.Search;

/// <summary>Why a search didn't run.</summary>
public enum SearchStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "search-scope-not-found", "Container or source not found")]
    NotFound,

    [Failure(400, "invalid-search", "Invalid search")]
    Invalid,
}

/// <summary>Which kind of scope a search may name.</summary>
public enum SearchScopeKind
{
    Any,
    Container,
    Source,
}

/// <summary>
/// What to search: a container or source by id or name. <see cref="Kind"/> limits which kind it may be;
/// one of the other kind reads as missing.
/// </summary>
public sealed record SearchScope(string IdOrName, SearchScopeKind Kind = SearchScopeKind.Any)
{
    public static SearchScope Container(Guid id) => new(id.ToString(), SearchScopeKind.Container);

    public static SearchScope Source(Guid id) => new(id.ToString(), SearchScopeKind.Source);
}

/// <summary>
/// One search. <see cref="TopK"/> null asks for <see cref="SearchKnowledge.DefaultTopK"/> results and
/// <see cref="MinScore"/> null uses the configured minimum; <see cref="Path"/> narrows the search to a folder
/// subtree unless <see cref="Filters"/> already names one.
/// </summary>
public sealed record SearchRequest(
    string? Query,
    SearchMode Mode = SearchMode.Hybrid,
    int? TopK = null,
    float? MinScore = null,
    string? Path = null,
    IReadOnlyDictionary<string, string>? Filters = null);

/// <summary>
/// Searches one container or source for the caller, from every surface. The request is checked before the
/// scope is looked up, so a bad one is answered the same way for a hidden source as for a missing id. A
/// source the caller may not see reads as missing, unless search checks its every hit for them (a
/// Confluence space): then it is searched by id, and reads as missing when they can read nothing in it.
/// A scope that reads as missing does the same work as one that is found, searching <see cref="Nowhere"/>
/// instead, so how long the answer takes can't tell a hidden source from an unknown id.
/// </summary>
[Operation("search.knowledge", Scope = Scope, Risk = RiskTier.Read)]
public sealed class SearchKnowledge(
    IContainerStore containers, ISourceStore sources, ISourceVisibility visibility, IKnowledgeSearch search,
    IOptionsMonitor<SearchSettings> settings, IOperationAuthorizer authorizer)
{
    public const string Scope = "knowledge:read";

    public const int DefaultTopK = 10;

    /// <summary>The scope searched for a scope that reads as missing: no container or source has this id.</summary>
    public static readonly Guid Nowhere = Guid.Empty;

    public async Task<Attempt<SearchResult, SearchStatus>> RunAsync(OperationContext context, SearchScope scope, SearchRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return Attempt<SearchResult, SearchStatus>.Fail(SearchStatus.Forbidden, "You don't have permission to search.");
        if (Refusal(request) is var (detail, field))
            return Attempt<SearchResult, SearchStatus>.Fail(SearchStatus.Invalid, detail, field);

        var owner = await ResolveAsync(context, scope, ct);
        var (ownerId, discoverable) = owner ?? (Nowhere, false);

        var filters = request.Filters is null ? [] : new Dictionary<string, string>(request.Filters);
        if (!string.IsNullOrWhiteSpace(request.Path))
            filters.TryAdd("pathPrefix", request.Path);

        var result = await search.SearchAsync(request.Query!, new SearchOptions(
            TopK: request.TopK ?? DefaultTopK,
            MinScore: request.MinScore ?? (float)settings.CurrentValue.MinimumScore,
            ContainerId: ownerId.ToString(),
            Mode: request.Mode,
            Filters: filters.Count > 0 ? filters : null,
            UserId: SearchPrincipal.Resolve(context.User)), ct);

        // A hidden source the caller can read nothing in answers as a missing id, so naming one can't confirm it exists.
        return owner is null || result.Hits.Count == 0 && !discoverable ? NotFound(scope) : Attempt<SearchResult, SearchStatus>.Succeed(result);
    }

    /// <summary>What is wrong with a request, and the field at fault; null when nothing is.</summary>
    private static (string Detail, string Field)? Refusal(SearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return ("query is required", "query");
        if (request.Query.Length > ValidationConstants.MaxQueryLength)
            return ($"query must not exceed {ValidationConstants.MaxQueryLength} characters", "query");
        if (request.TopK is < ValidationConstants.MinTopK or > ValidationConstants.MaxTopK)
            return ($"topK must be between {ValidationConstants.MinTopK} and {ValidationConstants.MaxTopK}", "topK");
        // Written so that NaN, which compares false with everything, is refused too.
        if (request.MinScore is { } minScore && !(minScore >= ValidationConstants.MinScore && minScore <= ValidationConstants.MaxScore))
            return ($"minScore must be between {ValidationConstants.MinScore:F1} and {ValidationConstants.MaxScore:F1}", "minScore");
        return null;
    }

    /// <summary>
    /// The id to search and whether the caller may know it exists, or null when it reads as missing.
    /// Containers are checked first, so an id both kinds somehow share resolves to the more restricted kind.
    /// </summary>
    private async Task<(Guid Id, bool Discoverable)?> ResolveAsync(OperationContext context, SearchScope scope, CancellationToken ct)
    {
        bool containersAllowed = scope.Kind != SearchScopeKind.Source;
        bool sourcesAllowed = scope.Kind != SearchScopeKind.Container;

        if (Guid.TryParse(scope.IdOrName, out var id))
        {
            if (containersAllowed && await containers.GetAsync(id, ct) is not null)
                return (id, true);
            if (!sourcesAllowed)
                return null;
            var source = await sources.GetAsync(id, ct);
            // Asked for an unknown id too, so a hidden source and a missing one cost the same.
            var canSee = await visibility.ForAsync(context.User, ct);
            if (source is null)
                return null;
            if (canSee(source))
                return (id, true);
            // By id only: a name lookup that answered differently for a hidden source would let anyone probe for one.
            return visibility.IsSearchableWhenHidden(source) ? (id, false) : null;
        }

        // Names are stored lower case.
        string name = scope.IdOrName.ToLowerInvariant();
        if (containersAllowed && await containers.GetByNameAsync(name, ct) is { } container)
            return Guid.TryParse(container.Id, out var containerId) ? (containerId, true) : null;
        if (!sourcesAllowed)
            return null;
        var named = await sources.GetByNameAsync(name, ct);
        var canSeeNamed = await visibility.ForAsync(context.User, ct);
        return named is not null && canSeeNamed(named) ? (named.Id, true) : null;
    }

    private static Attempt<SearchResult, SearchStatus> NotFound(SearchScope scope) =>
        Attempt<SearchResult, SearchStatus>.Fail(SearchStatus.NotFound, scope.Kind switch
        {
            SearchScopeKind.Container => $"Container {scope.IdOrName} not found",
            SearchScopeKind.Source => $"Source {scope.IdOrName} not found",
            _ => $"Container or source '{scope.IdOrName}' not found",
        });
}

/// <summary>Searches a container: <see cref="SearchKnowledge"/>, limited to containers.</summary>
[Operation("search.container", Scope = SearchKnowledge.Scope, Risk = RiskTier.Read)]
public sealed class SearchContainer(SearchKnowledge search)
{
    public Task<Attempt<SearchResult, SearchStatus>> RunAsync(OperationContext context, Guid containerId, SearchRequest request, CancellationToken ct = default) =>
        search.RunAsync(context, SearchScope.Container(containerId), request, ct);
}

/// <summary>Searches a source: <see cref="SearchKnowledge"/>, limited to sources.</summary>
[Operation("search.source", Scope = SearchKnowledge.Scope, Risk = RiskTier.Read)]
public sealed class SearchSource(SearchKnowledge search)
{
    public Task<Attempt<SearchResult, SearchStatus>> RunAsync(OperationContext context, Guid sourceId, SearchRequest request, CancellationToken ct = default) =>
        search.RunAsync(context, SearchScope.Source(sourceId), request, ct);
}

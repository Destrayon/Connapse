using Connapse.Application.Containers;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Vectors;
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

    [Failure(503, "embeddings-unavailable", "Embedding service unavailable")]
    EmbeddingsUnavailable,

    [Failure(409, "memory-container", "Agent memory container")]
    MemoryContainer,

    [Failure(503, "model-not-ready", "Search isn't ready yet")]
    ModelNotReady,
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
/// instead, so how long the answer takes can't tell a hidden source from an unknown id; and when the search
/// fails, a scope the caller may not know exists still reads as missing.
/// </summary>
[Operation("search.knowledge", Scope = Scope, Risk = RiskTier.Read)]
public sealed class SearchKnowledge(
    IContainerStore containers, ISourceStore sources, ISourceVisibility visibility, IKnowledgeSearch search,
    IOptionsMonitor<SearchSettings> settings, IOperationAuthorizer authorizer, ILocalModelMonitor? localModel = null,
    IDocumentStore? documents = null)
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
        if (owner?.Memory is { } memory)
            return Attempt<SearchResult, SearchStatus>.Fail(SearchStatus.MemoryContainer, ContainerRules.MemoryOnly(memory));
        var (ownerId, discoverable, _) = owner ?? (Nowhere, false, null);

        var filters = request.Filters is null ? [] : new Dictionary<string, string>(request.Filters);
        if (!string.IsNullOrWhiteSpace(request.Path))
            filters.TryAdd("pathPrefix", request.Path);

        SearchResult result;
        try
        {
            result = await search.SearchAsync(request.Query!, new SearchOptions(
                TopK: request.TopK ?? DefaultTopK,
                MinScore: request.MinScore ?? (float)settings.CurrentValue.MinimumScore,
                ContainerId: ownerId.ToString(),
                Mode: request.Mode,
                Filters: filters.Count > 0 ? filters : null,
                UserId: SearchPrincipal.Resolve(context.User)), ct);
        }
        catch (Exception ex) when (!discoverable && ex is not OperationCanceledException)
        {
            return NotFound(scope);
        }
        catch (EmbeddingUnavailableException ex) when (ex.InnerException is EmbeddingDimensionMismatchException mismatch)
        {
            // A settings problem, not an outage: retrying won't help, so say what to change.
            return Attempt<SearchResult, SearchStatus>.Fail(SearchStatus.EmbeddingsUnavailable,
                mismatch.Message + " Keyword mode still works meanwhile.");
        }
        catch (EmbeddingUnavailableException) when (localModel?.Current is { HoldsEmbedding: true } waiting)
        {
            return Attempt<SearchResult, SearchStatus>.Fail(SearchStatus.ModelNotReady,
                waiting.Explanation + " Keyword mode works meanwhile.");
        }
        catch (EmbeddingUnavailableException)
        {
            return Attempt<SearchResult, SearchStatus>.Fail(SearchStatus.EmbeddingsUnavailable,
                "The embedding service isn't reachable, so this search can't run. Try again shortly, or search in Keyword mode, which doesn't use it.");
        }

        // A hidden source the caller can read nothing in answers as a missing id, so naming one can't confirm it exists.
        if (owner is null || result.Hits.Count == 0 && !discoverable)
            return NotFound(scope);
        // Only for a scope the caller may know exists: a hidden source's backlog would say what is in it.
        if (discoverable)
            result = result with { PendingDocuments = await PendingAsync(ownerId, ct) };
        return Attempt<SearchResult, SearchStatus>.Succeed(result);
    }

    /// <summary>How many documents in the scope still wait to be indexed; zero when that can't be counted, which mustn't fail a search that ran.</summary>
    private async Task<int> PendingAsync(Guid ownerId, CancellationToken ct)
    {
        if (documents is null)
            return 0;
        try
        {
            return await documents.CountAwaitingIndexAsync(ownerId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
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
    /// <c>Memory</c> is the container when it is an agent memory container, which holds no documents to search.
    /// </summary>
    internal async Task<(Guid Id, bool Discoverable, Container? Memory)?> ResolveAsync(OperationContext context, SearchScope scope, CancellationToken ct)
    {
        bool containersAllowed = scope.Kind != SearchScopeKind.Source;
        bool sourcesAllowed = scope.Kind != SearchScopeKind.Container;

        if (Guid.TryParse(scope.IdOrName, out var id))
        {
            if (containersAllowed && await containers.GetAsync(id, ContainerRules.Viewer(context), ct) is { } byId)
                return (id, true, MemoryOf(byId));
            if (!sourcesAllowed)
                return null;
            var source = await sources.FindAsync(id, ct);
            // Asked for an unknown id too, so a hidden source and a missing one cost the same.
            var canSee = await visibility.ForAsync(context.User, ct);
            if (source is null)
                return null;
            if (canSee(source))
                return (id, true, null);
            // By id only: a name lookup that answered differently for a hidden source would let anyone probe for one.
            return visibility.IsSearchableWhenHidden(source) ? (id, false, null) : null;
        }

        // Names are stored lower case.
        string name = scope.IdOrName.ToLowerInvariant();
        if (containersAllowed && (await containers.FindByNameAsync(name, ContainerRules.Viewer(context), ct)).FirstOrDefault() is { } container)
            return Guid.TryParse(container.Id, out var containerId) ? (containerId, true, MemoryOf(container)) : null;
        if (!sourcesAllowed)
            return null;
        var named = await sources.FindByNameAsync(name, ct);
        var canSeeNamed = await visibility.ForAsync(context.User, ct);
        return named is not null && canSeeNamed(named) ? (named.Id, true, null) : null;
    }

    private static Container? MemoryOf(Container container) => container.Kind == ContainerKind.Memory ? container : null;

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

/// <summary>What a search says about documents it can't find yet, shared by the Search page and MCP.</summary>
public static class SearchNotes
{
    /// <summary>The sentence for <paramref name="pendingDocuments"/> documents still waiting to be indexed; null when there are none.</summary>
    public static string? Pending(int pendingDocuments) => pendingDocuments switch
    {
        <= 0 => null,
        1 => "1 document in the searched container or source is still waiting to be indexed, so search can't find it yet.",
        _ => $"{pendingDocuments} documents in the searched container or source are still waiting to be indexed, so search can't find them yet.",
    };
}

namespace Connapse.Core;

/// <param name="UserId">
/// Who is searching. Null when the caller could not be resolved to a person — an unauthenticated
/// request, or an agent with no user behind it.
/// </param>
/// <remarks>
/// Carried but not yet consumed: per-user permission filtering is #421, and until it lands every
/// search returns everything regardless of what this holds.
/// <para>
/// Nullable with a null default deliberately. A surface that forgets to supply it gets null, and
/// null is what the filter will deny — so the failure mode of forgetting is too little access
/// rather than too much.
/// </para>
/// </remarks>
public record SearchOptions(
    int TopK = 10,
    float MinScore = 0.0f,
    string? ContainerId = null,
    SearchMode Mode = SearchMode.Hybrid,
    Dictionary<string, string>? Filters = null,
    Guid? UserId = null);

/// <param name="PendingDocuments">
/// Documents in the searched container or source still waiting to be indexed, so none of their text can be in
/// <paramref name="Hits"/> yet (CON-277). Zero when the caller may not know the scope exists.
/// </param>
/// <param name="NoGoodMatch">
/// True when search found nothing that matches the query well: no hits, or hits whose scores say they are
/// unrelated. A caller can tell the user nothing relevant was found instead of presenting the hits as answers.
/// </param>
public record SearchResult(
    List<SearchHit> Hits,
    int TotalMatches,
    TimeSpan Duration,
    int PendingDocuments = 0,
    bool NoGoodMatch = false);

public record SearchHit(
    string ChunkId,
    string DocumentId,
    string Content,
    float Score,
    Dictionary<string, string> Metadata);

public enum SearchMode { Semantic, Keyword, Hybrid }

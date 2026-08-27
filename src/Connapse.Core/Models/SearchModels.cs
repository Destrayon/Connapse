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

public record SearchResult(
    List<SearchHit> Hits,
    int TotalMatches,
    TimeSpan Duration);

public record SearchHit(
    string ChunkId,
    string DocumentId,
    string Content,
    float Score,
    Dictionary<string, string> Metadata);

public enum SearchMode { Semantic, Keyword, Hybrid }

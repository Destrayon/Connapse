namespace AIKnowledge.Core;

public record SearchOptions(
    int TopK = 10,
    float MinScore = 0.0f,
    string? ContainerId = null,
    SearchMode Mode = SearchMode.Hybrid,
    Dictionary<string, string>? Filters = null);

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

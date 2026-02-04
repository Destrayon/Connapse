# API Surface

Public interfaces and contracts. Track breaking changes here.

---

## Core Interfaces

### IKnowledgeIngester

```csharp
public interface IKnowledgeIngester
{
    Task<IngestionResult> IngestAsync(Stream content, IngestionOptions options, CancellationToken ct = default);
    Task<IngestionResult> IngestFromPathAsync(string path, IngestionOptions options, CancellationToken ct = default);
    IAsyncEnumerable<IngestionProgress> IngestWithProgressAsync(Stream content, IngestionOptions options, CancellationToken ct = default);
}

public record IngestionOptions(
    string? FileName = null,
    string? ContentType = null,
    string? CollectionId = null,
    ChunkingStrategy Strategy = ChunkingStrategy.Semantic,
    Dictionary<string, string>? Metadata = null);

public record IngestionResult(string DocumentId, int ChunkCount, TimeSpan Duration, List<string> Warnings);
public record IngestionProgress(IngestionPhase Phase, double PercentComplete, string? Message);
public enum IngestionPhase { Parsing, Chunking, Embedding, Storing, Complete }
public enum ChunkingStrategy { Semantic, FixedSize, Recursive, DocumentAware }
```

### IKnowledgeSearch

```csharp
public interface IKnowledgeSearch
{
    Task<SearchResult> SearchAsync(string query, SearchOptions options, CancellationToken ct = default);
    IAsyncEnumerable<SearchHit> SearchStreamAsync(string query, SearchOptions options, CancellationToken ct = default);
}

public record SearchOptions(
    int TopK = 10,
    float MinScore = 0.7f,
    string? CollectionId = null,
    SearchMode Mode = SearchMode.Hybrid,
    Dictionary<string, string>? Filters = null);

public record SearchResult(List<SearchHit> Hits, int TotalMatches, TimeSpan Duration);
public record SearchHit(string ChunkId, string DocumentId, string Content, float Score, Dictionary<string, string> Metadata);
public enum SearchMode { Semantic, Keyword, Hybrid }
```

### IEmbeddingProvider

```csharp
public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default);
    int Dimensions { get; }
    string ModelId { get; }
}
```

### IAgentTool

```csharp
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParameterSchema { get; }
    Task<ToolResult> ExecuteAsync(JsonElement parameters, ToolContext context, CancellationToken ct = default);
}

public record ToolResult(bool Success, JsonElement? Data = null, string? Error = null, string? HumanReadable = null);
public record ToolContext(string? UserId, string? ConversationId, IServiceProvider Services);
```

### IAgentMemory

```csharp
public interface IAgentMemory
{
    Task SaveNoteAsync(string key, string content, NoteOptions? options = null, CancellationToken ct = default);
    Task<string?> GetNoteAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<Note>> SearchNotesAsync(string query, int topK = 5, CancellationToken ct = default);
    Task DeleteNoteAsync(string key, CancellationToken ct = default);
}

public record Note(string Key, string Content, DateTime Created, DateTime Modified, Dictionary<string, string> Metadata);
public record NoteOptions(string? Category = null, Dictionary<string, string>? Metadata = null, TimeSpan? Expiry = null);
```

---

## Breaking Changes

<!-- Log breaking changes with migration guidance -->

### vX.X.X — YYYY-MM-DD

**Change**: 

**Migration**: 

---

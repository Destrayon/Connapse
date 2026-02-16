namespace Connapse.Core;

public record IngestionOptions(
    string? DocumentId = null,
    string? FileName = null,
    string? ContentType = null,
    string? CollectionId = null,
    ChunkingStrategy Strategy = ChunkingStrategy.Semantic,
    Dictionary<string, string>? Metadata = null);

public record IngestionResult(
    string DocumentId,
    int ChunkCount,
    TimeSpan Duration,
    List<string> Warnings);

public record IngestionProgress(
    IngestionPhase Phase,
    double PercentComplete,
    string? Message);

/// <summary>
/// DTO for SignalR real-time ingestion progress updates.
/// Matches the structure sent by IngestionProgressBroadcaster.
/// </summary>
public record IngestionProgressUpdate(
    string JobId,
    string State,
    string? CurrentPhase,
    double PercentComplete,
    string? ErrorMessage,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public enum IngestionPhase { Parsing, Chunking, Embedding, Storing, Complete }

public enum ChunkingStrategy { Semantic, FixedSize, Recursive, DocumentAware }

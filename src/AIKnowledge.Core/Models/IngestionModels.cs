namespace AIKnowledge.Core;

public record IngestionOptions(
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

public enum IngestionPhase { Parsing, Chunking, Embedding, Storing, Complete }

public enum ChunkingStrategy { Semantic, FixedSize, Recursive, DocumentAware }

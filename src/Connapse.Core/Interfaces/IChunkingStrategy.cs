namespace Connapse.Core.Interfaces;

/// <summary>
/// Splits parsed document content into chunks suitable for embedding and storage.
/// </summary>
public interface IChunkingStrategy
{
    /// <summary>
    /// The name of this chunking strategy (e.g., "FixedSize", "Recursive", "Semantic").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Chunks a parsed document into smaller pieces.
    /// </summary>
    /// <param name="parsedDocument">The parsed document to chunk.</param>
    /// <param name="settings">Chunking configuration settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of chunks with content and metadata.</returns>
    Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Information about a chunk created during chunking.
/// </summary>
public record ChunkInfo(
    string Content,
    int ChunkIndex,
    int TokenCount,
    int StartOffset,
    int EndOffset,
    Dictionary<string, string> Metadata);

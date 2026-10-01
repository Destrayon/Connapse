namespace Connapse.Core.Interfaces;

/// <summary>
/// Parses document content from a stream into structured text suitable for chunking.
/// </summary>
public interface IDocumentParser
{
    /// <summary>
    /// File extensions this parser supports (e.g., ".txt", ".pdf", ".docx").
    /// </summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>
    /// Recorded on every document the parser indexes, so a different parser taking over an
    /// extension marks those documents for re-parsing.
    /// </summary>
    string Name => GetType().Name;

    /// <summary>
    /// Bump whenever the parser's output for the same file changes. Documents recorded with an
    /// older version are re-parsed by a reindex that detects settings changes; documents indexed
    /// before versions were recorded count as version 1.
    /// </summary>
    int Version => 1;

    /// <summary>
    /// Parses a document from a stream.
    /// </summary>
    /// <param name="stream">The file content stream.</param>
    /// <param name="fileName">The original file name (used for extension detection).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed document with extracted text and metadata.</returns>
    Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of parsing a document.
/// </summary>
public record ParsedDocument(
    string Content,
    Dictionary<string, string> Metadata,
    List<string> Warnings);

namespace AIKnowledge.Core.Interfaces;

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

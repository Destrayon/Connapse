namespace Connapse.Core.Interfaces;

public interface IKnowledgeIngester
{
    Task<IngestionResult> IngestAsync(Stream content, IngestionOptions options, CancellationToken ct = default);
    IAsyncEnumerable<IngestionProgress> IngestWithProgressAsync(Stream content, IngestionOptions options, CancellationToken ct = default);

    /// <summary>
    /// Resolves the source stream for a document (via the container's connector) and delegates
    /// to <see cref="IngestAsync(Stream, IngestionOptions, CancellationToken)"/>. Used by the
    /// Hangfire ingestion job class, which only has a document id when invoked.
    /// </summary>
    Task<IngestionResult> IngestByIdAsync(string documentId, IngestionOptions options, CancellationToken ct = default);
}

namespace Connapse.Core.Interfaces;

public interface IDocumentSummaryEmbeddingProvider
{
    Task<IReadOnlyList<DocumentWithSummary>> GetSummaryEmbeddingsAsync(
        IReadOnlyList<Document> docs,
        CancellationToken ct = default);
}

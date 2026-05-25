using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Summarization;

public sealed class DocumentSummaryEmbeddingProvider(
    IEmbeddingProvider embeddingProvider) : IDocumentSummaryEmbeddingProvider
{
    public async Task<IReadOnlyList<DocumentWithSummary>> GetSummaryEmbeddingsAsync(
        IReadOnlyList<Document> docs,
        CancellationToken ct = default)
    {
        List<DocumentWithSummary> result = new(docs.Count);
        foreach (Document d in docs)
        {
            if (string.IsNullOrEmpty(d.Summary)) continue;
            float[] embedding = await embeddingProvider.EmbedAsync(d.Summary, ct);
            result.Add(new DocumentWithSummary(Guid.Parse(d.Id), d.Summary, embedding));
        }
        return result;
    }
}

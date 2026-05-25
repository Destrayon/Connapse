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
        List<Document> docsWithSummaries = docs.Where(d => !string.IsNullOrEmpty(d.Summary)).ToList();
        if (docsWithSummaries.Count == 0) return Array.Empty<DocumentWithSummary>();

        // Batch all summaries in a single provider call instead of one-by-one.
        // For N=1000 docs this avoids ~100 s of serial latency and satisfies the 30 s budget.
        List<string> summaries = docsWithSummaries.Select(d => d.Summary!).ToList();
        IReadOnlyList<float[]> embeddings = await embeddingProvider.EmbedBatchAsync(summaries, ct);

        return docsWithSummaries.Zip(embeddings, (d, e) =>
            new DocumentWithSummary(Guid.Parse(d.Id), d.Summary!, e)).ToList();
    }
}

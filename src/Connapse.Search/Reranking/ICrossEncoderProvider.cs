namespace Connapse.Search.Reranking;

/// <summary>
/// Internal interface for cross-encoder reranking providers.
/// All providers accept a query + list of texts and return relevance scores.
/// </summary>
internal interface ICrossEncoderProvider
{
    /// <summary>
    /// Reranks documents by relevance to the query.
    /// Returns scores indexed by the original document position.
    /// </summary>
    Task<IReadOnlyList<CrossEncoderScore>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        int? topN,
        CancellationToken ct = default);
}

/// <summary>
/// A single reranking score result mapping an input document index to its relevance score.
/// </summary>
internal record CrossEncoderScore(int Index, float Score);

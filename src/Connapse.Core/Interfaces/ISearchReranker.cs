namespace Connapse.Core.Interfaces;

/// <summary>
/// Reranks search results to improve relevance.
/// </summary>
public interface ISearchReranker
{
    /// <summary>
    /// The name of this reranking strategy (e.g., "RRF", "CrossEncoder").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Reranks a list of search hits based on their relevance to the query.
    /// </summary>
    /// <param name="query">The original search query.</param>
    /// <param name="hits">The search hits to rerank.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reranked hits with updated scores.</returns>
    Task<List<SearchHit>> RerankAsync(
        string query,
        List<SearchHit> hits,
        CancellationToken cancellationToken = default);
}

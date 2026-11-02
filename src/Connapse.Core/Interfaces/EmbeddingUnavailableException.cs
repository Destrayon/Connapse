namespace Connapse.Core.Interfaces;

/// <summary>
/// A search query couldn't be embedded: the embedding service didn't answer, timed out, or refused the
/// request. Searches that need a query vector can't run until it is back; keyword search still can.
/// </summary>
public sealed class EmbeddingUnavailableException(Exception inner)
    : Exception($"The embedding service couldn't embed the search query: {inner.Message}", inner);

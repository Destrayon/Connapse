namespace Connapse.Core.Interfaces;

/// <summary>
/// Embedded table cards (CON-310), so a search finds a container's tables by what they're about and not only by the
/// words they share with it. Cards are embedded with the container's embedding model and kept until the card changes.
/// </summary>
public interface ITableCardIndex
{
    /// <summary>
    /// Embeds the cards whose stored vector is missing or stale. Best effort: a table loads without it, and a later
    /// search embeds what's missing.
    /// </summary>
    Task IndexAsync(Guid containerId, IReadOnlyList<TableCard> cards, CancellationToken ct = default);

    /// <summary>
    /// How similar each card is to <paramref name="query"/>, from -1 to 1, embedding missing or stale cards first, up to
    /// a cap per call. Cards left without a vector are counted in <see cref="TableCardSimilarities.Unembedded"/>; when
    /// the embedding model can't be reached at all, the result is <see cref="TableCardSimilarities.Unavailable"/>.
    /// </summary>
    Task<TableCardSimilarities> SimilaritiesAsync(
        Guid containerId, IReadOnlyList<TableCard> cards, string query, CancellationToken ct = default);
}

/// <summary>
/// Each card's similarity to a search, by table id. <see cref="Available"/> is false when nothing could be embedded,
/// and <see cref="Unembedded"/> counts the cards that have no score.
/// </summary>
public sealed record TableCardSimilarities(IReadOnlyDictionary<Guid, double> Scores, int Unembedded, bool Available)
{
    public static TableCardSimilarities Unavailable(int cards) => new(new Dictionary<Guid, double>(), cards, false);
}

using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace Connapse.Storage.Tables;

/// <summary>
/// Table cards embedded in <c>table_card_vectors</c> (CON-310), one row per table and embedding space, scored against
/// a search in SQL by cosine similarity. Only a vector made from the card as it is now is used: a stale one, made
/// before a column was hidden or the description changed, could rank a table by what callers may no longer see.
/// </summary>
public sealed class PgTableCardIndex(
    IDbContextFactory<KnowledgeDbContext> factory,
    IEmbeddingProvider embeddings,
    IOptionsMonitor<EmbeddingSettings> settings,
    ILogger<PgTableCardIndex> logger) : ITableCardIndex
{
    /// <summary>The most cards one search embeds before scoring; the rest wait for the next search or load.</summary>
    public const int MaxEmbedPerCall = 200;

    public async Task IndexAsync(Guid containerId, IReadOnlyList<TableCard> cards, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cards);
        try
        {
            await EmbedStaleAsync(containerId, cards, int.MaxValue, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't embed {Count} table cards in container {ContainerId}", cards.Count, containerId);
        }
    }

    public async Task<TableCardSimilarities> SimilaritiesAsync(
        Guid containerId, IReadOnlyList<TableCard> cards, string query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(query);
        if (cards.Count == 0)
            return new TableCardSimilarities(new Dictionary<Guid, double>(), 0, true);

        float[] vector;
        try
        {
            await EmbedStaleAsync(containerId, cards, MaxEmbedPerCall, ct);
            vector = await embeddings.EmbedAsync(query, EmbeddingInputType.Query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't embed table cards or the search in container {ContainerId}", containerId);
            return TableCardSimilarities.Unavailable(cards.Count);
        }

        int dims = vector.Length;
        await using var context = await factory.CreateDbContextAsync(ct);
        // Only vectors of the query's size are comparable, and only those made from the card as it is now.
        string sql = $"""
            SELECT v.table_id AS "TableId", 1 - (v.embedding::vector({dims}) <=> @query) AS "Similarity"
            FROM table_card_vectors v
            JOIN unnest(@ids, @hashes) AS c(id, hash) ON c.id = v.table_id AND c.hash = v.text_hash
            WHERE v.container_id = @container AND v.model_id = @model AND vector_dims(v.embedding) = {dims}
            """;
        List<CardScore> rows = await context.Database.SqlQueryRaw<CardScore>(sql,
                new NpgsqlParameter("@query", new Vector(vector)),
                new NpgsqlParameter("@ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = cards.Select(c => c.TableId).ToArray() },
                new NpgsqlParameter("@hashes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cards.Select(c => c.Hash).ToArray() },
                new NpgsqlParameter("@container", containerId),
                new NpgsqlParameter("@model", ModelId))
            .ToListAsync(ct);

        Dictionary<Guid, double> scores = rows.Where(r => !double.IsNaN(r.Similarity)).ToDictionary(r => r.TableId, r => r.Similarity);
        return new TableCardSimilarities(scores, cards.Count - scores.Count, scores.Count > 0);
    }

    private string ModelId => EmbeddingIdentity.For(settings.CurrentValue);

    /// <summary>Embeds up to <paramref name="limit"/> cards whose vector is missing or stale, in the order given.</summary>
    private async Task EmbedStaleAsync(Guid containerId, IReadOnlyList<TableCard> cards, int limit, CancellationToken ct)
    {
        string model = ModelId;
        Guid[] ids = [.. cards.Select(c => c.TableId).Distinct()];
        await using var context = await factory.CreateDbContextAsync(ct);
        Dictionary<Guid, TableCardVectorEntity> stored = await context.TableCardVectors
            .Where(v => v.ModelId == model && ids.Contains(v.TableId))
            .ToDictionaryAsync(v => v.TableId, ct);
        List<TableCard> stale = [.. cards
            .DistinctBy(c => c.TableId)
            .Where(c => !stored.TryGetValue(c.TableId, out TableCardVectorEntity? v) || v.TextHash != c.Hash)
            .Take(limit)];
        if (stale.Count == 0)
            return;

        IReadOnlyList<float[]> vectors = await embeddings.EmbedBatchAsync(stale.Select(c => c.Text), EmbeddingInputType.Document, ct);
        for (int i = 0; i < stale.Count; i++)
        {
            TableCard card = stale[i];
            if (stored.TryGetValue(card.TableId, out TableCardVectorEntity? row))
            {
                row.TextHash = card.Hash;
                row.Embedding = new Vector(vectors[i]);
                row.EmbeddedAt = DateTime.UtcNow;
            }
            else
            {
                context.TableCardVectors.Add(new TableCardVectorEntity
                {
                    TableId = card.TableId,
                    ModelId = model,
                    ContainerId = containerId,
                    TextHash = card.Hash,
                    Embedding = new Vector(vectors[i]),
                    EmbeddedAt = DateTime.UtcNow,
                });
            }
        }

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Another search or load embedded the same cards, or a table was deleted, meanwhile; theirs stands.
            logger.LogDebug(ex, "Table card vectors in container {ContainerId} changed while embedding", containerId);
        }
    }

    private sealed record CardScore(Guid TableId, double Similarity);
}

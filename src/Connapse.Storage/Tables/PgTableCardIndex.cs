using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
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
            string model = ModelId;
            Guid[] ids = [.. cards.Select(c => c.TableId).Distinct()];
            Dictionary<Guid, string> hashes;
            await using (var context = await factory.CreateDbContextAsync(ct))
            {
                hashes = await context.TableCardVectors
                    .Where(v => v.ModelId == model && ids.Contains(v.TableId))
                    .Select(v => new { v.TableId, v.TextHash })
                    .ToDictionaryAsync(v => v.TableId, v => v.TextHash, ct);
            }

            List<TableCard> stale = [.. cards.DistinctBy(c => c.TableId).Where(c => hashes.GetValueOrDefault(c.TableId) != c.Hash)];
            if (stale.Count > 0)
                await EmbedAndSaveAsync(containerId, model, stale, [.. hashes.Keys], ct);
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
            vector = await embeddings.EmbedAsync(query, EmbeddingInputType.Query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't embed the table search in container {ContainerId}", containerId);
            return TableCardSimilarities.Unavailable(cards.Count);
        }

        string model = ModelId;
        int dims = vector.Length;
        // One round trip that sends only the query vector: sending every card's id and hash made searches over 876
        // tables stall past their 50 ms bar. Hashes are compared here instead, and a vector of another size has no score.
        string sql = $"""
            SELECT table_id AS "TableId", text_hash AS "TextHash",
                   CASE WHEN vector_dims(embedding) = {dims} THEN 1 - (embedding::vector({dims}) <=> @query) END AS "Similarity"
            FROM table_card_vectors
            WHERE container_id = @container AND model_id = @model
            """;
        List<StoredScore> rows;
        await using (var context = await factory.CreateDbContextAsync(ct))
        {
            rows = await context.Database.SqlQueryRaw<StoredScore>(sql,
                    new NpgsqlParameter("@query", new Vector(vector)),
                    new NpgsqlParameter("@container", containerId),
                    new NpgsqlParameter("@model", model))
                .ToListAsync(ct);
        }

        Dictionary<Guid, StoredScore> stored = rows.ToDictionary(r => r.TableId);
        Dictionary<Guid, double> scores = [];
        List<TableCard> stale = [];
        foreach (TableCard card in cards.DistinctBy(c => c.TableId))
        {
            if (!stored.TryGetValue(card.TableId, out StoredScore? row) || row.TextHash != card.Hash)
                stale.Add(card);
            else if (row.Similarity is double similarity && !double.IsNaN(similarity))
                scores[card.TableId] = similarity;
        }

        if (stale.Count > 0)
        {
            List<TableCard> batch = [.. stale.Take(MaxEmbedPerCall)];
            try
            {
                IReadOnlyList<float[]> vectors = await EmbedAndSaveAsync(containerId, model, batch, [.. stored.Keys], ct);
                for (int i = 0; i < batch.Count; i++)
                    if (Cosine(vector, vectors[i]) is double similarity)
                        scores[batch[i].TableId] = similarity;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A stale card stays unscored rather than ranked by its old vector.
                logger.LogWarning(ex, "Couldn't embed {Count} table cards in container {ContainerId}", batch.Count, containerId);
            }
        }

        return new TableCardSimilarities(scores, cards.Count - scores.Count, scores.Count > 0);
    }

    private string ModelId => EmbeddingIdentity.For(settings.CurrentValue);

    /// <summary>Embeds <paramref name="stale"/> and saves each vector, updating the rows already in <paramref name="existing"/>.</summary>
    private async Task<IReadOnlyList<float[]>> EmbedAndSaveAsync(
        Guid containerId, string model, List<TableCard> stale, HashSet<Guid> existing, CancellationToken ct)
    {
        IReadOnlyList<float[]> vectors = await embeddings.EmbedBatchAsync(stale.Select(c => c.Text), EmbeddingInputType.Document, ct);

        await using var context = await factory.CreateDbContextAsync(ct);
        Guid[] staleIds = [.. stale.Select(c => c.TableId).Where(existing.Contains)];
        Dictionary<Guid, TableCardVectorEntity> rows = staleIds.Length == 0
            ? []
            : await context.TableCardVectors
                .Where(v => v.ModelId == model && staleIds.Contains(v.TableId))
                .ToDictionaryAsync(v => v.TableId, ct);

        for (int i = 0; i < stale.Count; i++)
        {
            TableCard card = stale[i];
            if (rows.TryGetValue(card.TableId, out TableCardVectorEntity? row))
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

        return vectors;
    }

    /// <summary>Cosine similarity as pgvector's <c>1 - (a &lt;=&gt; b)</c> gives it; none when sizes differ or a vector is zero.</summary>
    private static double? Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return null;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return na == 0 || nb == 0 ? null : dot / Math.Sqrt(na * nb);
    }

    private sealed record StoredScore(Guid TableId, string TextHash, double? Similarity);
}

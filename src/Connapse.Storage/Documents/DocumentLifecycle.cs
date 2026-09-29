using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Connapse.Storage.Documents;

/// <summary>
/// PostgreSQL implementation of <see cref="IDocumentLifecycle"/>. Each transition is a single
/// <c>UPDATE … WHERE &lt;expected state&gt; RETURNING</c>, so two workers racing for the same
/// document cannot both win, and a job working on a superseded generation cannot overwrite the
/// newer one's outcome.
/// </summary>
public sealed class DocumentLifecycle(
    IDbContextFactory<KnowledgeDbContext> factory,
    ILogger<DocumentLifecycle> logger,
    IIngestionStateBroadcaster? broadcaster = null) : IDocumentLifecycle
{
    private const string Queued = nameof(DocumentStatus.Queued);
    private const string Processing = nameof(DocumentStatus.Processing);

    public async Task<int?> EnqueuedAsync(Guid documentId, bool resetAttempts, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var rows = await RunAsync(context, """
            UPDATE documents SET
                ingestion_status  = @queued,
                status_changed_at = now(),
                generation        = generation + 1,
                error_message     = CASE WHEN @reset THEN NULL ELSE error_message END,
                attempt_count     = CASE WHEN @reset THEN 0 ELSE attempt_count END,
                job_id            = NULL
            WHERE id = @id
            RETURNING ingestion_status AS "Status", summary_status AS "SummaryStatus", generation AS "Generation"
            """,
            ct,
            new NpgsqlParameter("id", documentId),
            new NpgsqlParameter("queued", Queued),
            new NpgsqlParameter("reset", resetAttempts));

        await PublishAsync(documentId, rows, ct);
        return rows.Count == 0 ? null : rows[0].Generation;
    }

    public async Task<bool> TryClaimAsync(Guid documentId, int generation, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var rows = await RunAsync(context, """
            UPDATE documents SET
                ingestion_status  = @processing,
                status_changed_at = now(),
                attempt_count     = attempt_count + 1
            WHERE id = @id
              AND ingestion_status = @queued
              AND (@gen = 0 OR generation = @gen)
            RETURNING ingestion_status AS "Status", summary_status AS "SummaryStatus", generation AS "Generation"
            """,
            ct,
            new NpgsqlParameter("id", documentId),
            new NpgsqlParameter("gen", generation),
            new NpgsqlParameter("queued", Queued),
            new NpgsqlParameter("processing", Processing));

        return await PublishAsync(documentId, rows, ct);
    }

    public async Task<bool> CompleteAsync(Guid documentId, int generation, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        var rows = await CompleteAsync(context, documentId, generation, ct);
        return await PublishAsync(documentId, rows, ct);
    }

    /// <summary>
    /// The Processing → Ready update on a caller's context, so the pipeline can commit it in the
    /// same transaction that swaps the document's chunks. Returns no rows when the guard failed.
    /// The caller announces it with <see cref="NotifyAsync"/> once the transaction has committed.
    /// </summary>
    public static Task<List<StatusRow>> CompleteAsync(
        KnowledgeDbContext context, Guid documentId, int generation, CancellationToken ct) =>
        RunAsync(context, """
            UPDATE documents SET
                ingestion_status  = @ready,
                status_changed_at = now(),
                error_message     = NULL,
                attempt_count     = 0,
                last_indexed_at   = now()
            WHERE id = @id
              AND ingestion_status = @processing
              AND (@gen = 0 OR generation = @gen)
            RETURNING ingestion_status AS "Status", summary_status AS "SummaryStatus", generation AS "Generation"
            """,
            ct,
            new NpgsqlParameter("id", documentId),
            new NpgsqlParameter("gen", generation),
            new NpgsqlParameter("ready", nameof(DocumentStatus.Ready)),
            new NpgsqlParameter("processing", Processing));

    public async Task<bool> RetryScheduledAsync(Guid documentId, int generation, string error, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var rows = await RunAsync(context, """
            UPDATE documents SET
                ingestion_status  = @queued,
                status_changed_at = now(),
                error_message     = @error
            WHERE id = @id
              AND ingestion_status = @processing
              AND (@gen = 0 OR generation = @gen)
            RETURNING ingestion_status AS "Status", summary_status AS "SummaryStatus", generation AS "Generation"
            """,
            ct,
            new NpgsqlParameter("id", documentId),
            new NpgsqlParameter("gen", generation),
            new NpgsqlParameter("error", error),
            new NpgsqlParameter("queued", Queued),
            new NpgsqlParameter("processing", Processing));

        return await PublishAsync(documentId, rows, ct);
    }

    public async Task<bool> FailAsync(
        Guid documentId, int generation, DocumentStatus failedStatus, string error, CancellationToken ct = default)
    {
        if (!failedStatus.IsFailed())
            throw new ArgumentOutOfRangeException(nameof(failedStatus), failedStatus, "Must be a failed status.");

        await using var context = await factory.CreateDbContextAsync(ct);

        var rows = await RunAsync(context, """
            UPDATE documents SET
                ingestion_status  = @failed,
                status_changed_at = now(),
                error_message     = @error
            WHERE id = @id
              AND ingestion_status IN (@queued, @processing)
              AND (@gen = 0 OR generation = @gen)
            RETURNING ingestion_status AS "Status", summary_status AS "SummaryStatus", generation AS "Generation"
            """,
            ct,
            new NpgsqlParameter("id", documentId),
            new NpgsqlParameter("gen", generation),
            new NpgsqlParameter("error", error),
            new NpgsqlParameter("failed", failedStatus.ToString()),
            new NpgsqlParameter("queued", Queued),
            new NpgsqlParameter("processing", Processing));

        return await PublishAsync(documentId, rows, ct);
    }

    public async Task RecordJobAsync(Guid documentId, int generation, string jobId, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        // No status change and nothing to announce. Guarded so a slow enqueue cannot stamp its
        // job onto a newer generation that another enqueue already owns.
        await context.Database.ExecuteSqlRawAsync("""
            UPDATE documents SET job_id = @job
            WHERE id = @id AND (@gen = 0 OR generation = @gen) AND ingestion_status IN (@queued, @processing)
            """,
            [
                new NpgsqlParameter("id", documentId),
                new NpgsqlParameter("gen", generation),
                new NpgsqlParameter("job", jobId),
                new NpgsqlParameter("queued", Queued),
                new NpgsqlParameter("processing", Processing),
            ],
            ct);
    }

    public async Task SetSummaryStatusAsync(Guid documentId, SummaryStatus status, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var rows = await RunAsync(context, """
            UPDATE documents SET summary_status = @summary
            WHERE id = @id
            RETURNING ingestion_status AS "Status", summary_status AS "SummaryStatus", generation AS "Generation"
            """,
            ct,
            new NpgsqlParameter("id", documentId),
            new NpgsqlParameter("summary", status.ToString()));

        await PublishAsync(documentId, rows, ct);
    }

    public async Task NotifyAsync(Guid documentId, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var rows = await RunAsync(context, """
            SELECT ingestion_status AS "Status", summary_status AS "SummaryStatus", generation AS "Generation"
            FROM documents WHERE id = @id
            """,
            ct,
            new NpgsqlParameter("id", documentId));

        await PublishAsync(documentId, rows, ct);
    }

    private static async Task<List<StatusRow>> RunAsync(
        KnowledgeDbContext context, string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        // Enumerated rather than composed: EF would wrap a composed UPDATE … RETURNING in a
        // subquery, which PostgreSQL rejects.
        var rows = new List<StatusRow>(1);
        await foreach (var row in context.Database
            .SqlQueryRaw<StatusRow>(sql, parameters.Cast<object>().ToArray())
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    private async Task<bool> PublishAsync(Guid documentId, List<StatusRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return false;
        if (broadcaster is null) return true;

        try
        {
            await broadcaster.BroadcastStatusChangedAsync(
                documentId.ToString(),
                Enum.Parse<DocumentStatus>(rows[0].Status),
                Enum.Parse<SummaryStatus>(rows[0].SummaryStatus),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The transition is committed; a UI push that failed must not undo the caller's work.
            logger.LogWarning(ex, "Failed to broadcast status change for document {DocumentId}", documentId);
        }

        return true;
    }

    public sealed record StatusRow(string Status, string SummaryStatus, int Generation);
}

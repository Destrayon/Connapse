using Connapse.Core;
using Hangfire;

namespace Connapse.Background.Jobs;

/// <summary>
/// Hangfire job handlers for the ingestion pipeline.
///
/// [Queue] attributes are on the interface methods (not just impl) because
/// Hangfire's job activator resolves attributes from the call-site method —
/// which is the interface method when callers use Enqueue&lt;IIngestionJobs&gt;(...).
/// </summary>
public interface IIngestionJobs
{
    /// <summary>
    /// Claims the document, then parse + chunk + embed + swap. The pipeline marks it Ready in the
    /// same transaction as the chunk swap; this job records failures and schedules retries.
    /// </summary>
    [Queue(JobQueues.Ingestion)]
    Task IngestAsync(string documentId, IngestionOptions options, CancellationToken ct);

    /// <summary>
    /// Re-enqueues documents left Queued or Processing whose job is gone — a crashed worker, an
    /// enqueue that failed after the status was written, a job deleted from the dashboard.
    /// </summary>
    [Queue(JobQueues.Default)]
    Task RequeueStuckDocumentsAsync(CancellationToken ct);
}

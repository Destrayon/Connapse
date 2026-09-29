using Connapse.Core;
using Hangfire;

namespace Connapse.Background.Jobs;

/// <summary>
/// Hangfire job handlers for the ingestion pipeline + per-doc summarization.
/// Two methods because Hangfire invokes by interface+method name; keeping them
/// together lets us chain them via ContinueJobWith.
///
/// [Queue] attributes are on the interface methods (not just impl) because
/// Hangfire's job activator resolves attributes from the call-site method —
/// which is the interface method when callers use Enqueue&lt;IIngestionJobs&gt;(...).
/// </summary>
public interface IIngestionJobs
{
    /// <summary>
    /// Claims the document, then parse + chunk + embed + swap. The pipeline marks it Ready in the
    /// same transaction as the chunk swap; this job records failures and schedules retries and the
    /// per-doc summary.
    /// </summary>
    [Queue(JobQueues.Ingestion)]
    Task IngestAsync(string documentId, IngestionOptions options, CancellationToken ct);

    /// <summary>
    /// Runs the per-doc LLM summary from the document's stored chunks. Honors
    /// SummarySettings.Enabled and records the outcome in the document's summary status only.
    /// </summary>
    [Queue(JobQueues.Summarization)]
    Task PerDocSummaryAsync(string documentId, CancellationToken ct);

    /// <summary>
    /// Re-enqueues documents left Queued or Processing whose job is gone — a crashed worker, an
    /// enqueue that failed after the status was written, a job deleted from the dashboard.
    /// </summary>
    [Queue(JobQueues.Default)]
    Task RequeueStuckDocumentsAsync(CancellationToken ct);
}

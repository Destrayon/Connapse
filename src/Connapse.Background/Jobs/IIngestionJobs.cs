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
    /// Parse + chunk + embed + save. On success, transitions document to
    /// IngestionState.Indexed. PerDocSummary is set up as a Hangfire ContinueWith
    /// at enqueue time (in HangfireIngestionQueue), not from inside this method.
    /// </summary>
    [Queue(JobQueues.Ingestion)]
    Task IngestAsync(string documentId, IngestionOptions options, CancellationToken ct);

    /// <summary>
    /// Runs the per-doc LLM summary. Honors SummarySettings.Enabled. On success,
    /// transitions document to IngestionState.SummaryIndexed and schedules a
    /// debounced container rollup. Triggered as ContinueWith on IngestAsync success.
    /// </summary>
    [Queue(JobQueues.Summarization)]
    Task PerDocSummaryAsync(string documentId, CancellationToken ct);
}

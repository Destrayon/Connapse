using Hangfire;

namespace Connapse.Background.Jobs;

/// <summary>
/// Hangfire job handlers for container-level rollup operations.
///
/// [Queue] attributes are on the interface methods (not just impl) because
/// Hangfire's job activator resolves attributes from the call-site method —
/// which is the interface method when callers use Enqueue&lt;ISummaryJobs&gt;(...).
/// </summary>
public interface ISummaryJobs
{
    /// <summary>
    /// Roll up per-doc summaries into a container summary. Guarded by
    /// DisableConcurrentExecution(key: "container-rollup-{0}") so only one rollup
    /// per container runs at a time. Checks doc_set_hash and short-circuits when
    /// no real change occurred since the last rollup.
    /// </summary>
    [Queue(JobQueues.Summarization)]
    Task RollupContainerAsync(Guid containerId, CancellationToken ct);

    /// <summary>
    /// Hourly safety-net sweep. Finds containers with stale summaries
    /// (newer doc summaries than container summary), enqueues a RollupContainerAsync
    /// for each. DisableConcurrentExecution prevents thundering herd.
    /// </summary>
    [Queue(JobQueues.Summarization)]
    Task SweepStaleContainersAsync(CancellationToken ct);
}

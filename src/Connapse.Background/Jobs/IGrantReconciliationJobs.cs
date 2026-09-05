using Hangfire;

namespace Connapse.Background.Jobs;

/// <summary>
/// Hangfire handler for the recurring orphaned-access-grant cleanup.
/// </summary>
/// <remarks>
/// The <c>[Queue]</c> attribute is on the interface method, as with the other job interfaces,
/// because Hangfire reads it there when the recurring job is registered against the interface.
/// </remarks>
public interface IGrantReconciliationJobs
{
    [Queue(JobQueues.Default)]
    Task ReconcileAsync(CancellationToken ct);
}

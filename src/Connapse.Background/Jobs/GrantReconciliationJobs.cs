using Connapse.Core;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Connapse.Background.Jobs;

/// <summary>
/// Recurring sweep that deletes S3 access grants no connection needs any more.
/// </summary>
/// <remarks>
/// Thin: every safety decision lives in <see cref="IGrantReconciliationService"/>. Mirrors
/// <see cref="SummaryJobs.SweepStaleContainersAsync"/> — recurring, no Hangfire retry (a failed tick
/// just waits for the next), and non-reentrant so two ticks never delete against each other.
/// </remarks>
public sealed class GrantReconciliationJobs(
    IGrantReconciliationService reconciler,
    ILogger<GrantReconciliationJobs> logger) : IGrantReconciliationJobs
{
    [Queue(JobQueues.Default)]
    [AutomaticRetry(Attempts = 0)] // Recurring; on failure just wait until the next tick.
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task ReconcileAsync(CancellationToken ct)
    {
        var report = await reconciler.ReconcileAsync(enforce: true, ct);

        // Quiet on a clean, do-nothing tick; noisy when something happened or was held back.
        if (report.Deleted > 0 || report.Aborted.Count > 0 || report.Failed.Count > 0)
        {
            logger.LogInformation(
                "Grant reconcile: scanned {Scanned}, orphaned {Orphaned}, deleted {Deleted}, "
                + "held back {Aborted}, failed {Failed}",
                report.Scanned, report.Orphaned, report.Deleted,
                report.Aborted.Count, report.Failed.Count);
        }
    }
}

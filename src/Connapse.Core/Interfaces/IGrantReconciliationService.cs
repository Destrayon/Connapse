namespace Connapse.Core;

/// <summary>The outcome of one reconcile run.</summary>
/// <param name="Scanned">Grants read across all regions.</param>
/// <param name="Orphaned">Group grants whose scope no connection covers.</param>
/// <param name="Deleted">Grants actually removed (0 when not enforcing).</param>
/// <param name="Aborted">
/// Reasons a run, or one region, deleted nothing — an incomplete connection view, a tripped circuit
/// breaker, or an unreadable region. Non-empty means cleanup held back on purpose.
/// </param>
/// <param name="Failed">Grants a delete call rejected.</param>
public record ReconcileReport(
    int Scanned, int Orphaned, int Deleted,
    IReadOnlyList<string> Aborted, IReadOnlyList<GrantWriteFailure> Failed)
{
    /// <summary>A run that deleted nothing because it could not act safely.</summary>
    public static ReconcileReport Abort(string reason) => new(0, 0, 0, [reason], []);
}

/// <summary>
/// Deletes S3 Access Grants Connapse created that no connection needs any more.
/// </summary>
/// <remarks>
/// A Core interface so the Hangfire job (in the Background layer) can depend on it while the
/// implementation lives in the Web layer with the AWS plumbing and the admin page it also serves.
/// </remarks>
public interface IGrantReconciliationService
{
    /// <summary>
    /// Reconciles once. <paramref name="enforce"/> false computes and logs what it would delete
    /// without deleting; true deletes.
    /// </summary>
    Task<ReconcileReport> ReconcileAsync(bool enforce, CancellationToken ct = default);
}

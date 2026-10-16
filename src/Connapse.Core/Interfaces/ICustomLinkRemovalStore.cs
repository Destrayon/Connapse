namespace Connapse.Core.Interfaces;

/// <summary>
/// The outbox of unlink calls owed to custom providers when a Connapse user is deleted. Links live only in
/// the middle men, so each must be told; an entry stays until its middle man acknowledges.
/// </summary>
public interface ICustomLinkRemovalStore
{
    /// <summary>
    /// Queues an unlink for this user at every custom provider that offers linking, due now. Written before
    /// the user is deleted. Returns how many were queued.
    /// </summary>
    Task<int> EnqueueForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Drops this user's queued unlinks: the deletion they were queued for did not happen.</summary>
    Task CancelForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Entries whose next attempt is due, oldest first.</summary>
    Task<IReadOnlyList<CustomLinkRemoval>> DueAsync(DateTime now, int take, CancellationToken ct = default);

    /// <summary>The middle man acknowledged: the entry is done.</summary>
    Task CompleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Records a failed attempt and when to try again; <paramref name="alerted"/> marks that the failure was reported.</summary>
    Task RetryLaterAsync(Guid id, DateTime nextAttemptAt, string error, bool alerted, CancellationToken ct = default);
}

/// <summary>One unlink owed to a custom provider for a deleted user.</summary>
public sealed record CustomLinkRemoval(Guid Id, Guid ProviderId, Guid UserId, DateTime CreatedAt, int Attempts, DateTime? AlertedAt);

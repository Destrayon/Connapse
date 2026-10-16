namespace Connapse.Core.Interfaces;

/// <summary>
/// The outbox of unlink calls owed to custom providers when a Connapse user is deleted. Links live only in
/// the middle men, so each must be told; an entry stays until its middle man acknowledges. Each entry keeps
/// the middle man's address and token from when it was queued, so editing or deleting the provider cannot
/// redirect or drop it.
/// </summary>
public interface ICustomLinkRemovalStore
{
    /// <summary>
    /// Queues an unlink for this user at every custom provider that offers linking, waiting for the deletion:
    /// nothing is sent until <see cref="ActivateForUserAsync"/>. Written before the user is deleted, so a crash
    /// after the deletion cannot lose it. Returns how many were queued.
    /// </summary>
    Task<int> EnqueueForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>The user is deleted: their waiting unlinks are due now.</summary>
    Task ActivateForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Drops this user's waiting unlinks: the deletion they were queued for did not happen.</summary>
    Task CancelForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Users with unlinks still waiting for a deletion queued before <paramref name="queuedBefore"/>.</summary>
    Task<IReadOnlyList<Guid>> WaitingUsersAsync(DateTime queuedBefore, CancellationToken ct = default);

    /// <summary>Entries whose next attempt is due, oldest first.</summary>
    Task<IReadOnlyList<CustomLinkRemoval>> DueAsync(DateTime now, int take, CancellationToken ct = default);

    /// <summary>The middle man acknowledged: the entry is done.</summary>
    Task CompleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Records a failed attempt and when to try again; <paramref name="alerted"/> marks that the failure was reported.</summary>
    Task RetryLaterAsync(Guid id, DateTime nextAttemptAt, string error, bool alerted, CancellationToken ct = default);
}

/// <summary>One unlink owed to a custom provider's middle man for a deleted user.</summary>
/// <param name="BaseUrl">The middle man's address when the unlink was queued.</param>
/// <param name="Token">The provider's token when the unlink was queued; null when there was none or it can no longer be decrypted.</param>
public sealed record CustomLinkRemoval(
    Guid Id, Guid ProviderId, Guid UserId, string BaseUrl, string? Token, DateTime CreatedAt, int Attempts, DateTime? AlertedAt);

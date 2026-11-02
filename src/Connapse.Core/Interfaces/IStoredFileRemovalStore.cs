namespace Connapse.Core.Interfaces;

/// <summary>
/// The outbox of stored files owed a removal after the records pointing to them were deleted. Entries are saved in
/// the same transaction as the deletion, so a removal that fails, or that a crash cuts off, is still owed and is
/// retried until it succeeds (CON-100).
/// </summary>
public interface IStoredFileRemovalStore
{
    /// <summary>Entries whose next attempt is due, oldest first.</summary>
    Task<IReadOnlyList<StoredFileRemoval>> DueAsync(DateTime now, int take, CancellationToken ct = default);

    /// <summary>The file is gone, or a new record points to it again: the entry is done.</summary>
    Task CompleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Records a failed attempt and when to try again.</summary>
    Task RetryLaterAsync(Guid id, DateTime nextAttemptAt, string error, CancellationToken ct = default);
}

/// <summary>One stored file owed a removal.</summary>
public sealed record StoredFileRemoval(Guid Id, Guid ContainerId, string Path, DateTime CreatedAt, int Attempts);

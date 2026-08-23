namespace Connapse.Core;

/// <summary>
/// Identifies who owns a document: a managed container or an external source. Exists so the
/// ownership decision is made once, at the point the owner is known, rather than by threading
/// a Guid and a boolean through every layer and hoping each call site pairs them correctly.
/// <para>
/// <see cref="ContainerId"/> and <see cref="SourceId"/> are exactly the two columns behind the
/// ck_documents_single_owner CHECK constraint — precisely one is ever non-null.
/// </para>
/// </summary>
public record OwnerRef(Guid Id, bool IsSource)
{
    public static OwnerRef ForContainer(Guid id) => new(id, IsSource: false);
    public static OwnerRef ForSource(Guid id) => new(id, IsSource: true);

    public Guid? ContainerId => IsSource ? null : Id;
    public Guid? SourceId => IsSource ? Id : null;
}

/// <summary>
/// Outcome of one sync cycle for one source.
/// </summary>
/// <param name="AlreadyRunning">
/// True when the cycle did nothing because another sync of the same source held the gate.
/// Distinct from <paramref name="Error"/>: nothing went wrong, the work simply belongs to
/// the cycle already in flight. A caller that surfaces this to a user should say "in
/// progress", not "failed".
/// </param>
public record SourceSyncResult(
    int Upserted,
    int Deleted,
    bool UsedDeltaPath,
    bool RequiredResync,
    string? Error,
    bool AlreadyRunning = false,
    int WithheldDeletions = 0);

/// <summary>
/// Thrown when a reindex would move a document between ownership domains — source to
/// container or the reverse. Distinct from ordinary ingestion failures because it must
/// escape the catch-all that records a "Failed" document: a refused reindex is not the
/// same as one that went wrong, and conflating them would leave the caller believing the
/// work failed rather than that it was rejected.
/// </summary>
public class DocumentOwnershipChangedException(Guid documentId, OwnerRef existing, OwnerRef attempted)
    : InvalidOperationException(
        $"Document {documentId} is owned by {(existing.IsSource ? "source" : "container")} {existing.Id}; " +
        $"refusing to reindex it as {(attempted.IsSource ? "source" : "container")} {attempted.Id}. " +
        "Document ownership cannot change.")
{
    public Guid DocumentId { get; } = documentId;
    public OwnerRef ExistingOwner { get; } = existing;
    public OwnerRef AttemptedOwner { get; } = attempted;
}


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
public record SourceSyncResult(
    int Upserted,
    int Deleted,
    bool UsedDeltaPath,
    bool RequiredResync,
    string? Error);

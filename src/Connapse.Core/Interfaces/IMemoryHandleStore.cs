namespace Connapse.Core.Interfaces;

/// <summary>
/// Short, permanent fact handles (f1, f2, ...) per person, so results stay compact and the save tools can refuse an ID
/// the caller was never shown. The owner is the user, or the agent when it acts for no user.
/// </summary>
public interface IMemoryHandleStore
{
    /// <summary>Records that these facts were shown to the owner, giving new ones the next numbers, and returns every one's number.</summary>
    Task<IReadOnlyDictionary<Guid, int>> ShowAsync(Guid owner, IReadOnlyCollection<Guid> factIds, CancellationToken ct = default);

    /// <summary>The fact behind a handle ("f12") or a full fact ID, or null when the owner was never shown it.</summary>
    Task<Guid?> ResolveAsync(Guid owner, string idOrHandle, CancellationToken ct = default);
}

namespace Connapse.Storage.Data.Entities;

/// <summary>
/// The short handle (f1, f2, ...) a memory fact got the first time a memory tool showed it to one person. Owned by the
/// user, or by the agent when it acts for no user. A handle never changes, and its number is never given out again.
/// </summary>
public class MemoryFactHandleEntity
{
    public Guid OwnerId { get; set; }
    public int Handle { get; set; }
    public Guid FactId { get; set; }
}

namespace Connapse.Storage.Data.Entities;

/// <summary>
/// One person's star on a memory space or container. Owned as a personal space is: by the user, or by the agent when
/// it acts for no user, so exactly one of the two is set.
/// </summary>
public class MemorySpaceStarEntity
{
    public Guid Id { get; set; }
    public Guid ContainerId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? AgentId { get; set; }
    public DateTimeOffset StarredAt { get; set; }
}

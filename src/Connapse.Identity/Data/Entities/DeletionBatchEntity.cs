namespace Connapse.Identity.Data.Entities;

/// <summary>Where a batch of deletions an agent asked for stands (CON-315).</summary>
public enum DeletionBatchStatus
{
    /// <summary>Waiting for its owner; it expires unanswered at <see cref="DeletionBatchEntity.ExpiresAt"/>.</summary>
    Pending,

    /// <summary>Approved, and the server is deleting the approved items.</summary>
    Running,

    /// <summary>Approved, and every approved item has an outcome.</summary>
    Done,

    /// <summary>The owner denied the whole batch; nothing was deleted.</summary>
    Denied,
}

/// <summary>What one item of a batch names.</summary>
public enum DeletionItemKind
{
    File,
    Folder,
    MemorySpace,

    /// <summary>A whole documents container, which must already be empty (CON-330).</summary>
    Container,

    /// <summary>One memory fact with every version of it (CON-330).</summary>
    Fact,
}

/// <summary>What became of one item of a batch.</summary>
public enum DeletionItemState
{
    /// <summary>Waiting with its batch.</summary>
    Pending,

    /// <summary>Approved; the server hasn't reached it yet.</summary>
    Approved,

    Deleted,

    /// <summary>Approved, but it was gone or had changed since the request, so it was left alone.</summary>
    Skipped,

    /// <summary>Approved, but the delete was refused or failed; <see cref="DeletionBatchItemEntity.Outcome"/> says why.</summary>
    Failed,

    /// <summary>The owner approved others in the batch but not this one.</summary>
    NotApproved,

    /// <summary>The owner denied the whole batch.</summary>
    Denied,
}

/// <summary>
/// Several deletions an agent asked for in one request, each with a reason, for the token's owner to approve
/// or deny with one click on the approvals page (CON-315). Once approved, the server deletes the approved items
/// itself, as the owner, and records each outcome for the agent to read.
/// </summary>
public class DeletionBatchEntity
{
    public Guid Id { get; set; }

    /// <summary>The token that asked, as <c>McpApprovalClaims.TokenKey</c> names it. Only it reads the batch's outcome.</summary>
    public string TokenKey { get; set; } = string.Empty;

    /// <summary>The person the token acts for, and the only one who may decide.</summary>
    public Guid OwnerUserId { get; set; }

    public DeletionBatchStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>A pending batch expires a day after it was made.</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime? DecidedAt { get; set; }
    public Guid? DecidedByUserId { get; set; }

    /// <summary>What the owner said, if anything; the agent reads it.</summary>
    public string? DecisionReason { get; set; }

    public DateTime? CompletedAt { get; set; }

    public List<DeletionBatchItemEntity> Items { get; set; } = [];

    public const int MaxItems = 50;
}

/// <summary>One file, folder, memory space, container or memory fact in a <see cref="DeletionBatchEntity"/>.</summary>
public class DeletionBatchItemEntity
{
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }

    /// <summary>Its place in the request, so the page and the agent see items in the order asked.</summary>
    public int Position { get; set; }

    public DeletionItemKind Kind { get; set; }

    /// <summary>The container that holds the file, folder or fact, or the memory space or container itself.</summary>
    public Guid ContainerId { get; set; }

    /// <summary>The file's document id, the folder's path, the fact's id, or empty for a memory space or container.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>What the approver sees: the file's path, the folder's path, or the space's name, with its container.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Why the agent wants it deleted.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the item's state when it was requested. The item is deleted only if it still has this
    /// fingerprint when the batch runs; otherwise it changed since the owner saw it, and is skipped.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    public DeletionItemState State { get; set; }

    /// <summary>What happened to it, in words the agent and the owner can act on.</summary>
    public string? Outcome { get; set; }

    public DateTime? FinishedAt { get; set; }

    public const int ReasonMaxLength = 300;
    public const int LabelMaxLength = 512;
    public const int TargetMaxLength = 1024;
    public const int OutcomeMaxLength = 500;
}

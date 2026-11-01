namespace Connapse.Identity.Data.Entities;

/// <summary>Where an MCP approval request stands.</summary>
public enum McpApprovalStatus
{
    Pending,
    Approved,
    Denied,
    Used,
}

/// <summary>
/// A high-impact MCP tool call (Destructive or Credential) a token asked to make, waiting for a person to
/// approve it in the UI (#782). It is bound to the token, the tool and the exact arguments: only that same
/// call from that same token runs, once, and only within <see cref="ExpiresAt"/>.
/// </summary>
public class McpApprovalEntity
{
    public Guid Id { get; set; }

    /// <summary>The token that asked: "pat:{id}", "agent_key:{id}", "oauth:{hash of user and client}" for an OAuth token, "session:{id}" for a browser session, or "signin:{id}" for a sign-in token.</summary>
    public string TokenKey { get; set; } = string.Empty;

    /// <summary>The person the token acts for: a PAT's user, an agent's creator, or the signed-in user.</summary>
    public Guid OwnerUserId { get; set; }

    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the redacted arguments behind a version prefix, so it carries nothing derived from a secret
    /// (CON-185). A request with no <see cref="ArgumentsProtected"/> (no secret in it, or made before CON-185) holds
    /// the SHA-256 of the full arguments instead. Emptied once the request is denied, used or expired.
    /// </summary>
    public string ArgumentsHash { get; set; } = string.Empty;

    /// <summary>The arguments as shown to the approver, with every secret field hidden, cut short if long.</summary>
    public string ArgumentsSummary { get; set; } = string.Empty;

    /// <summary>
    /// DataProtection ciphertext (purpose <c>McpApproval.v1</c>) of the full arguments, set only when redacting
    /// hid something: the retried call must unprotect to exactly these. Cleared once the request is denied or used.
    /// </summary>
    public string? ArgumentsProtected { get; set; }

    public McpApprovalStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>A pending request expires a day after it was made; an approved one, 15 minutes after approval.</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime? DecidedAt { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public DateTime? UsedAt { get; set; }
}

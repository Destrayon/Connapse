using System.Security.Claims;
using Connapse.Identity.Authentication;
using Connapse.Identity.Services;

namespace Connapse.Application.Approvals;

/// <summary>
/// A high-impact MCP call waiting for a person, as the approvals page shows it. <paramref name="IsYours"/> is whether the
/// viewer owns it and so may decide it; an administrator sees everyone's but decides only their own (CON-295).
/// </summary>
public sealed record McpApprovalItem(Guid Id, string ToolName, string ArgumentsSummary, string TokenKey, DateTime CreatedAt, DateTime ExpiresAt, bool IsYours);

public enum McpApprovalOutcome
{
    Success,

    [Failure(401, "approval-not-signed-in", "Sign in to review approvals")]
    NotSignedIn,

    [Failure(403, "approval-ui-only", "Approvals are decided in the Connapse UI")]
    NotFromUi,

    [Failure(404, "approval-not-found", "Approval request not found")]
    NotFound,

    [Failure(409, "approval-not-pending", "Approval request already decided or expired")]
    NotPending,
}

internal static class Approver
{
    /// <summary>
    /// The signed-in person deciding, and whether they see everyone's requests. Only the UI decides:
    /// a token must never approve the call it is waiting on, so REST and MCP callers are refused.
    /// </summary>
    public static (Guid UserId, bool IsAdmin)? From(OperationContext context) =>
        McpApprovalClaims.IsBrowserSession(context.User) && !McpApprovalClaims.IsToken(context.User) && Guid.TryParse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
            ? (id, context.User.IsInRole("Owner") || context.User.IsInRole("Admin"))
            : null;
}

/// <summary>The MCP calls waiting for the caller to approve them: their own, or everyone's for an administrator (#782).</summary>
[Operation("approvals.list", Scope = "approvals:decide", Risk = RiskTier.Read)]
public sealed class ListMcpApprovals(IMcpApprovalStore store)
{
    public async Task<Attempt<IReadOnlyList<McpApprovalItem>, McpApprovalOutcome>> ExecuteAsync(OperationContext context, CancellationToken ct = default)
    {
        if (context.Surface != OperationSurface.Ui)
            return Attempt<IReadOnlyList<McpApprovalItem>, McpApprovalOutcome>.Fail(McpApprovalOutcome.NotFromUi);
        if (Approver.From(context) is not var (userId, isAdmin))
            return Attempt<IReadOnlyList<McpApprovalItem>, McpApprovalOutcome>.Fail(McpApprovalOutcome.NotSignedIn);

        var pending = await store.ListPendingAsync(userId, isAdmin, ct);
        return Attempt<IReadOnlyList<McpApprovalItem>, McpApprovalOutcome>.Succeed(
            pending.Select(a => new McpApprovalItem(a.Id, a.ToolName, a.ArgumentsSummary, a.TokenKey, a.CreatedAt, a.ExpiresAt, a.OwnerUserId == userId)).ToList());
    }
}

/// <summary>
/// Approves or denies one waiting MCP call (#782). Approving lets that exact call from that token run
/// once within 15 minutes. Only its owner may decide, not an administrator (CON-295), and only in the UI.
/// </summary>
[Operation("approvals.decide", Scope = "approvals:decide", Risk = RiskTier.Write)]
public sealed class DecideMcpApproval(IMcpApprovalStore store)
{
    /// <param name="reason">What the person says about the decision; the agent that asked reads it (CON-292).</param>
    public async Task<Attempt<bool, McpApprovalOutcome>> ExecuteAsync(OperationContext context, Guid id, bool approve, string? reason = null, CancellationToken ct = default)
    {
        if (context.Surface != OperationSurface.Ui)
            return Attempt<bool, McpApprovalOutcome>.Fail(McpApprovalOutcome.NotFromUi);
        if (Approver.From(context) is not var (userId, _))
            return Attempt<bool, McpApprovalOutcome>.Fail(McpApprovalOutcome.NotSignedIn);

        // Someone else's request reads as not found, so its existence isn't revealed.
        return await store.DecideAsync(id, userId, approve, reason, ct) switch
        {
            McpApprovalDecision.Decided => Attempt<bool, McpApprovalOutcome>.Succeed(approve),
            McpApprovalDecision.NotPending => Attempt<bool, McpApprovalOutcome>.Fail(McpApprovalOutcome.NotPending),
            _ => Attempt<bool, McpApprovalOutcome>.Fail(McpApprovalOutcome.NotFound),
        };
    }
}

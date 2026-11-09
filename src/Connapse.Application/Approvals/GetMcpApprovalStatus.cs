using Connapse.Identity.Authentication;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using StoredStatus = Connapse.Identity.Data.Entities.McpApprovalStatus;

namespace Connapse.Application.Approvals;

/// <summary>Where a held call's approval request stands, as the agent that asked sees it.</summary>
public enum McpApprovalState
{
    /// <summary>No one has decided yet.</summary>
    Pending,

    /// <summary>Approved and not yet used: repeat the same call now and it runs once.</summary>
    Approved,

    /// <summary>A person denied it; <see cref="McpApprovalRequestStatus.Reason"/> says why when they gave a reason.</summary>
    Denied,

    /// <summary>No one decided in time, or the approval lapsed before the call was repeated.</summary>
    Expired,

    /// <summary>Approved, and the repeated call has run.</summary>
    Used,
}

/// <summary>
/// One approval request as the agent that asked sees it: what was decided, the person's reason, and in
/// <see cref="Message"/> what to do next.
/// </summary>
public sealed record McpApprovalRequestStatus(
    Guid Id, string ToolName, McpApprovalState State, string? Reason, DateTime CreatedAt, DateTime ExpiresAt, DateTime? DecidedAt, DateTime? UsedAt, string Message);

public enum McpApprovalStatusOutcome
{
    Success,

    [Failure(404, "approval-not-found", "Approval request not found")]
    NotFound,
}

/// <summary>
/// Tells the agent whether a person approved its held call (CON-292), optionally waiting for the decision. Only
/// the token that made the request can see it: any other caller, an administrator included, reads not found, so
/// a request's existence isn't revealed. It never runs the call: an approved call runs when the agent repeats it
/// with its real arguments, which the server keeps only sealed, and only for matching.
/// </summary>
[Operation("approvals.status", Scope = "none", Risk = RiskTier.Read)]
public sealed class GetMcpApprovalStatus(IMcpApprovalStore store, TimeProvider clock)
{
    /// <summary>The longest one call waits, kept under the common 60-second client timeout for a tool call or request.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(50);

    /// <summary>How often a waiting call looks again.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);

    /// <param name="wait">How long to wait for a decision while the request is pending; capped at <see cref="MaxWait"/>.</param>
    public async Task<Attempt<McpApprovalRequestStatus, McpApprovalStatusOutcome>> ExecuteAsync(
        OperationContext context, Guid id, TimeSpan wait = default, CancellationToken ct = default)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return NotFound(id);

        string tokenKey = McpApprovalClaims.TokenKey(context.User);
        var deadline = clock.GetUtcNow() + (wait > MaxWait ? MaxWait : wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
        while (true)
        {
            if (await store.GetForTokenAsync(id, tokenKey, ct) is not { } request)
                return NotFound(id);

            var now = clock.GetUtcNow();
            var status = Describe(request, now.UtcDateTime);
            if (status.State != McpApprovalState.Pending || now >= deadline)
                return Attempt<McpApprovalRequestStatus, McpApprovalStatusOutcome>.Succeed(status);

            var left = deadline - now;
            await Task.Delay(left < PollEvery ? left : PollEvery, clock, ct);
        }
    }

    /// <summary>The request's state as of <paramref name="now"/>, and what the agent should do about it.</summary>
    public static McpApprovalRequestStatus Describe(McpApprovalEntity request, DateTime now)
    {
        string call = $"'{request.ToolName}'";
        var (state, message) = request.Status switch
        {
            StoredStatus.Pending when request.ExpiresAt > now => (McpApprovalState.Pending,
                $"Request {request.Id} for {call} is still waiting for a person to approve it at /approvals. "
                + $"It expires at {request.ExpiresAt:u}. Check again, or ask the person to decide."),
            StoredStatus.Pending => (McpApprovalState.Expired,
                $"Request {request.Id} for {call} expired at {request.ExpiresAt:u} without a decision. "
                + "Repeat the call to ask for approval again."),
            StoredStatus.Approved when request.ExpiresAt > now => (McpApprovalState.Approved,
                $"Approved{Said(request.DecisionReason)}. Repeat the exact same {call} call with the same arguments now: it runs once, "
                + $"and the approval lapses at {request.ExpiresAt:u}."),
            StoredStatus.Approved => (McpApprovalState.Expired,
                $"Request {request.Id} for {call} was approved, but the approval lapsed at {request.ExpiresAt:u} before the call was repeated. "
                + "Repeat the call to ask for approval again."),
            StoredStatus.Denied => (McpApprovalState.Denied,
                $"Denied{Said(request.DecisionReason)}. Don't repeat the {call} call: it would only ask for approval again."),
            _ => (McpApprovalState.Used,
                $"Approved{Said(request.DecisionReason)}, and the repeated {call} call already ran{(request.UsedAt is { } used ? $" at {used:u}" : "")}."),
        };
        return new McpApprovalRequestStatus(request.Id, request.ToolName, state, request.DecisionReason, request.CreatedAt, request.ExpiresAt,
            request.DecidedAt, request.UsedAt, message);
    }

    private static Attempt<McpApprovalRequestStatus, McpApprovalStatusOutcome> NotFound(Guid id) =>
        Attempt<McpApprovalRequestStatus, McpApprovalStatusOutcome>.Fail(McpApprovalStatusOutcome.NotFound,
            $"Approval request {id} was not found. Only the token that made a request can check it.");

    private static string Said(string? reason) => reason is null ? ", no reason given" : $", with the reason: \"{reason}\"";
}

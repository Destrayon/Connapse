using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Audit;

/// <summary>Why reading the audit log didn't succeed.</summary>
public enum AuditReadStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(400, "invalid-page", "Invalid page")]
    InvalidPage,

    [Failure(400, "invalid-audit-filter", "Invalid audit filter")]
    InvalidFilter,
}

/// <summary>
/// Pages through the audit log for administrators, newest first: who did what to which resource, and
/// when. The entries say what users did, so only administrators read them.
/// </summary>
[Operation("audit.list", Scope = Scope, Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListAuditLog(IAuditLogReader reader, IOperationAuthorizer authorizer)
{
    public const string Scope = "admin:audit";

    public const int MaxTake = 200;

    public async Task<Attempt<AuditPage, AuditReadStatus>> RunAsync(OperationContext context, AuditQuery query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.AuditReader))
            return Attempt<AuditPage, AuditReadStatus>.Fail(AuditReadStatus.Forbidden, "Only administrators can read the audit log.");
        if (query.Skip < 0)
            return Attempt<AuditPage, AuditReadStatus>.Fail(AuditReadStatus.InvalidPage, "skip must be >= 0", "skip");
        if (query.Take is < 1 or > MaxTake)
            return Attempt<AuditPage, AuditReadStatus>.Fail(AuditReadStatus.InvalidPage, $"take must be between 1 and {MaxTake}", "take");
        if (query.Since is { } since && query.Until is { } until && since >= until)
            return Attempt<AuditPage, AuditReadStatus>.Fail(AuditReadStatus.InvalidFilter, "since must be before until", "since");

        return Attempt<AuditPage, AuditReadStatus>.Succeed(await reader.ListAsync(query with
        {
            Since = Utc(query.Since),
            Until = Utc(query.Until),
        }, ct));
    }

    /// <summary>A time given without a zone is UTC, as the log stores it; one with a zone is converted.</summary>
    private static DateTime? Utc(DateTime? time) => time switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } t => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        { } t => t.ToUniversalTime(),
    };
}

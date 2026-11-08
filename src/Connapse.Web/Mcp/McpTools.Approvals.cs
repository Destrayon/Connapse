using System.ComponentModel;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Approvals;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace Connapse.Web.Mcp;

/// <summary>
/// What became of a call held for a person's approval (CON-292). Any token may ask, since any token's call can be held,
/// but it only ever sees its own requests: the operation answers not found for anyone else's.
/// </summary>
public partial class McpTools
{
    internal const string ApprovalStatusName = "approval_status";

    [McpServerTool(Name = ApprovalStatusName, ReadOnly = true, Idempotent = true),
     Authorize, McpToolRisk(RiskTier.Read),
     Description("Check whether a person approved a call of yours that was held for approval, by the request id the held call gave you. "
        + "Pass `waitSeconds` (up to 50) to wait for the decision instead of asking again. The answer's `state` is pending, approved, denied, expired or used, "
        + "and `message` says what to do next: when approved, repeat the exact same call with the same arguments and it runs once; when denied, `reason` "
        + "holds what the person said, and repeating the call would only ask again.")]
    public static async Task<string> ApprovalStatus(
        GetMcpApprovalStatus status,
        IHttpContextAccessor http,
        [Description("The request id the held call returned")] string requestId,
        [Description("Seconds to wait for a decision while the request is pending, 0 to 50. Default 0: answer at once")] int waitSeconds = 0,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(requestId, out Guid id))
            return "Error: requestId must be the request id (a UUID) the held call returned.";

        var found = await status.ExecuteAsync(Mcp(http), id, TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, (int)GetMcpApprovalStatus.MaxWait.TotalSeconds)), ct);
        return found.Succeeded ? JsonSerializer.Serialize(found.Result, MemoryJson) : Refused(found);
    }
}

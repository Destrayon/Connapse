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

    private static readonly JsonSerializerOptions DeletionItemJson = new(JsonSerializerDefaults.Web);

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

    [McpServerTool(Name = "request_deletions"),
     Authorize(Policy = "Scope:KnowledgeWrite"), McpToolRisk(RiskTier.Write),
     Description("Ask your owner to approve several deletions at once instead of one held call each: up to 50 items, each a file "
        + "(kind \"file\", container, target = file id or path), a folder (kind \"folder\", container, target = folder path) or a memory space "
        + "(kind \"memory_space\", container = the space's id or name), with a short reason the owner reads. Every item is checked now; if any "
        + "can't be deleted nothing is asked, and the error names each bad item. Nothing is deleted yet: the owner approves all, some or none "
        + "on the Approvals page, and Connapse then deletes the approved items itself, skipping any that are gone or changed since you asked. "
        + "Call deletion_status with the returned id (and waitSeconds) to read each item's outcome; don't delete the items yourself.")]
    public static async Task<string> RequestDeletions(
        RequestDeletions request,
        IHttpContextAccessor http,
        [Description("The deletions, in the order the owner should see them: a JSON array of {\"kind\", \"container\", \"target\", \"reason\"} objects")] JsonElement items,
        CancellationToken ct = default)
    {
        DeletionRequestItem[]? parsed;
        try
        {
            parsed = items.ValueKind == JsonValueKind.Array ? items.Deserialize<DeletionRequestItem[]>(DeletionItemJson) : null;
        }
        catch (JsonException)
        {
            parsed = null;
        }
        if (parsed is null)
            return "Error: items must be a JSON array of {\"kind\", \"container\", \"target\", \"reason\"} objects.";

        var asked = await request.ExecuteAsync(Mcp(http), parsed, ct);
        return asked.Succeeded ? JsonSerializer.Serialize(asked.Result, MemoryJson) : Refused(asked);
    }

    [McpServerTool(Name = "deletion_status", ReadOnly = true, Idempotent = true),
     Authorize, McpToolRisk(RiskTier.Read),
     Description("Check a batch of deletions you asked for with request_deletions, by its id. Pass `waitSeconds` (up to 50) to wait while "
        + "it is pending or running. `state` is pending, expired, denied, running or done; each item's `state` is deleted, skipped (gone or "
        + "changed since you asked), failed, not_approved or denied, and its `outcome` says why. `message` says what to do next.")]
    public static async Task<string> DeletionStatus(
        GetDeletionBatchStatus status,
        IHttpContextAccessor http,
        [Description("The batch id request_deletions returned")] string batchId,
        [Description("Seconds to wait while the batch is pending or running, 0 to 50. Default 0: answer at once")] int waitSeconds = 0,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(batchId, out Guid id))
            return "Error: batchId must be the batch id (a UUID) request_deletions returned.";

        var found = await status.ExecuteAsync(Mcp(http), id, TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, (int)GetMcpApprovalStatus.MaxWait.TotalSeconds)), ct);
        return found.Succeeded ? JsonSerializer.Serialize(found.Result, MemoryJson) : Refused(found);
    }
}

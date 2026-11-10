using System.Security.Cryptography;
using System.Text;
using Connapse.Application.Containers;
using Connapse.Application.Files;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Authentication;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Microsoft.Extensions.Logging;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Application.Approvals;

/// <summary>
/// One deletion an agent asks for. <paramref name="Kind"/> is file, folder, memory_space, container or fact.
/// <paramref name="Container"/> is the container's id or name, or for a memory space or container its own; a fact
/// is found by its id alone, so its container is ignored. <paramref name="Target"/> is the file's id or path, the
/// folder's path, or the fact's id; a memory space or container has none.
/// </summary>
public sealed record DeletionRequestItem(string Kind, string Container, string? Target, string Reason);

/// <summary>The body of POST /api/approvals/deletions.</summary>
public sealed record RequestDeletionsBody(IReadOnlyList<DeletionRequestItem>? Items);

/// <summary>One item of a batch, as the agent and the approvals page see it.</summary>
public sealed record DeletionBatchItemView(
    Guid Id, int Position, string Kind, Guid ContainerId, string Target, string Label, string Reason, string State, string? Outcome);

/// <summary>
/// A batch of deletions. <paramref name="State"/> is pending, expired, denied, running or done; <paramref name="Message"/>
/// says what the agent should do next. <paramref name="IsYours"/> is whether the viewer owns it and so may decide it.
/// </summary>
public sealed record DeletionBatchView(
    Guid Id, string State, DateTime CreatedAt, DateTime ExpiresAt, DateTime? DecidedAt, string? Reason,
    IReadOnlyList<DeletionBatchItemView> Items, string Message, bool IsYours, string TokenKey);

public enum DeletionBatchOutcome
{
    Success,

    [Failure(400, "invalid-deletion-batch", "Invalid deletion request")]
    Invalid,

    [Failure(401, "approval-not-signed-in", "Sign in to review approvals")]
    NotSignedIn,

    [Failure(403, "approval-ui-only", "Approvals are decided in the Connapse UI")]
    NotFromUi,

    [Failure(404, "deletion-batch-not-found", "Deletion batch not found")]
    NotFound,

    [Failure(409, "deletion-batch-not-pending", "Deletion batch already decided or expired")]
    NotPending,

    [Failure(400, "deletion-batch-nothing-selected", "No items selected")]
    NothingSelected,
}

/// <summary>Finding a batch's items, fingerprinting them, and describing a batch (CON-315).</summary>
public static class DeletionTargets
{
    public static string KindName(DeletionItemKind kind) => kind switch
    {
        DeletionItemKind.File => "file",
        DeletionItemKind.Folder => "folder",
        DeletionItemKind.Container => "container",
        DeletionItemKind.Fact => "fact",
        _ => "memory_space",
    };

    public static DeletionItemKind? ParseKind(string? kind) => kind?.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "file" => DeletionItemKind.File,
        "folder" => DeletionItemKind.Folder,
        "memory_space" or "memoryspace" or "space" => DeletionItemKind.MemorySpace,
        "container" => DeletionItemKind.Container,
        "fact" or "memory_fact" => DeletionItemKind.Fact,
        _ => null,
    };

    public static string StateName(DeletionItemState state) => state switch
    {
        DeletionItemState.NotApproved => "not_approved",
        _ => state.ToString().ToLowerInvariant(),
    };

    /// <summary>The space as its owner sees it, so the fingerprint is the same whoever asked and whoever runs it.</summary>
    public static MemoryCaller OwnerViewer(Guid ownerUserId) => new(ownerUserId, null, null);

    public static string FileFingerprint(Document document) => Hash(
        $"file\n{document.Id}\n{document.ContainerId}\n{document.Path}\n{document.FileName}\n{document.SizeBytes}\n{document.CreatedAt.Ticks}\n{document.ContentType}");

    public static string FolderFingerprint(Guid containerId, string path, bool recorded, IEnumerable<Document> inside)
    {
        StringBuilder text = new($"folder\n{containerId}\n{path}\n{recorded}\n");
        foreach (Document document in inside.OrderBy(d => d.Id, StringComparer.Ordinal))
            text.Append(document.Id).Append('|').Append(document.Path).Append('|').Append(document.SizeBytes).Append('|').Append(document.CreatedAt.Ticks).Append('\n');
        return Hash(text.ToString());
    }

    public static string SpaceFingerprint(Container space, MemorySpaceImpact impact) =>
        Hash($"space\n{space.Id}\n{space.Name}\n{space.UpdatedAt.Ticks}\n{impact.Facts}\n{impact.Versions}");

    /// <summary>A container may only be deleted empty, so its files are part of what the owner approved.</summary>
    public static string ContainerFingerprint(Container container, IEnumerable<Document> inside)
    {
        StringBuilder text = new($"container\n{container.Id}\n{container.Name}\n{container.UpdatedAt.Ticks}\n");
        foreach (Document document in inside.OrderBy(d => d.Id, StringComparer.Ordinal))
            text.Append(document.Id).Append('\n');
        return Hash(text.ToString());
    }

    /// <summary>Every version of the fact the owner can see: a correction or a new version changes what would be deleted.</summary>
    public static string FactFingerprint(Guid factId, MemoryHistoryPage history)
    {
        StringBuilder text = new($"fact\n{factId}\n{history.Total}\n");
        foreach (RecalledFact version in history.Versions.OrderBy(v => v.Fact.Id))
            text.Append(version.Fact.Id).Append('|').Append(version.Fact.ContainerId).Append('|').Append(version.Fact.Subject).Append('|')
                .Append(version.Fact.Attribute).Append('|').Append(version.Fact.Value).Append('|').Append(version.Fact.SupersededAt?.UtcTicks).Append('\n');
        return Hash(text.ToString());
    }

    /// <summary>How many versions of one fact are read to fingerprint it; a chain longer than this can't be batched.</summary>
    public const int MaxFactVersions = 100;

    /// <summary>The fact's versions as <paramref name="viewer"/> sees them, or null when it is missing, hidden, or too long a chain.</summary>
    public static async Task<(Guid SpaceId, MemoryHistoryPage History)?> FactAsync(IMemoryService memory, Guid factId, MemoryCaller viewer, CancellationToken ct)
    {
        if (await memory.SpaceOfAsync(factId, ct) is not Guid space)
            return null;
        var history = await memory.HistoryAsync(space, factId, null, MaxFactVersions, viewer, ct);
        return history.NotFound || history.Denied || history.Versions.Count == 0 || history.Next is not null ? null : (space, history);
    }

    public static string FactLabel(string spaceName, MemoryFact fact)
    {
        string label = $"{spaceName}: {fact.Subject} · {fact.Attribute} = {fact.Value}";
        return label.Length <= DeletionBatchItemEntity.LabelMaxLength ? label : label[..(DeletionBatchItemEntity.LabelMaxLength - 3)] + "...";
    }

    internal static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static DeletionBatchView View(DeletionBatchEntity batch, DateTime now, Guid? viewer = null)
    {
        bool expired = batch.Status == DeletionBatchStatus.Pending && batch.ExpiresAt <= now;
        var items = batch.Items.OrderBy(i => i.Position)
            .Select(i => new DeletionBatchItemView(i.Id, i.Position, KindName(i.Kind), i.ContainerId, i.Target, i.Label, i.Reason,
                expired && i.State == DeletionItemState.Pending ? "expired" : StateName(i.State), i.Outcome))
            .ToList();

        int deleted = batch.Items.Count(i => i.State == DeletionItemState.Deleted);
        int skipped = batch.Items.Count(i => i.State == DeletionItemState.Skipped);
        int failed = batch.Items.Count(i => i.State == DeletionItemState.Failed);
        int notApproved = batch.Items.Count(i => i.State == DeletionItemState.NotApproved);
        string said = batch.DecisionReason is null ? "" : $" The owner said: \"{batch.DecisionReason}\".";
        var (state, message) = batch.Status switch
        {
            DeletionBatchStatus.Pending when !expired => ("pending",
                $"Batch {batch.Id} of {batch.Items.Count} deletions is waiting for its owner to approve it at /approvals. "
                + $"It expires at {batch.ExpiresAt:u}. Check again with a wait, or ask the owner to decide."),
            DeletionBatchStatus.Pending => ("expired",
                $"Batch {batch.Id} expired at {batch.ExpiresAt:u} without a decision; nothing was deleted. Ask again if the deletions are still wanted."),
            DeletionBatchStatus.Denied => ("denied",
                $"The owner denied batch {batch.Id}; nothing was deleted.{said} Don't ask for these deletions again unless the owner asks you to."),
            DeletionBatchStatus.Running => ("running",
                $"Approved; Connapse is deleting the approved items now ({deleted} deleted so far).{said} Check again shortly."),
            _ => ("done",
                $"Done: {deleted} deleted, {skipped} skipped because they were gone or had changed since the request, {failed} failed, "
                + $"{notApproved} not approved.{said} Each item's outcome says what happened to it. Don't delete skipped or "
                + "not-approved items another way: ask again if they still need to go."),
        };
        return new DeletionBatchView(batch.Id, state, batch.CreatedAt, batch.ExpiresAt, batch.DecidedAt, batch.DecisionReason, items, message,
            viewer is Guid v && v == batch.OwnerUserId, batch.TokenKey);
    }
}

/// <summary>
/// Asks the token's owner to approve several deletions at once (CON-315): up to 50 files, folders or memory spaces,
/// each with a reason. Every item is checked now, as the caller: it must exist and the caller must be allowed to
/// delete it, or nothing is asked. Nothing is deleted until the owner approves on the approvals page; then Connapse
/// deletes the approved items itself and the caller reads each outcome with approvals.batches.status.
/// </summary>
[Operation("approvals.batches.request", Scope = "knowledge:write", Risk = RiskTier.Write)]
public sealed class RequestDeletions(
    IDeletionBatchStore batches,
    IContainerStore containers,
    IDocumentStore documents,
    IDocumentAccess access,
    IFolderStore folders,
    IMemorySpaceStore spaces,
    GetContainer getContainer,
    DeleteMemorySpace deleteSpace,
    IOperationAuthorizer authorizer,
    IAuditLogger audit,
    TimeProvider clock,
    IMemoryService memory)
{
    public async Task<Attempt<DeletionBatchView, DeletionBatchOutcome>> ExecuteAsync(
        OperationContext context, IReadOnlyList<DeletionRequestItem>? items, CancellationToken ct = default)
    {
        if (context.User.Identity?.IsAuthenticated != true || McpApprovalClaims.OwnerUserId(context.User) is not Guid owner)
            return Fail(DeletionBatchOutcome.NotSignedIn, "Sign in, or call with a token, to ask for deletions.");
        if (items is null || items.Count == 0)
            return Fail(DeletionBatchOutcome.Invalid, "Name at least one file, folder or memory space to delete.", "items");
        if (items.Count > DeletionBatchEntity.MaxItems)
            return Fail(DeletionBatchOutcome.Invalid, $"A batch holds at most {DeletionBatchEntity.MaxItems} items; split the request.", "items");

        bool writer = await authorizer.AllowsAsync(context.User, Policies.Writer) && await authorizer.AllowsAsync(context.User, Policies.KnowledgeWrite);
        List<NewDeletionItem> resolved = [];
        List<string> problems = [];
        HashSet<(DeletionItemKind, Guid, string)> seen = [];
        for (int i = 0; i < items.Count; i++)
        {
            var (item, problem) = writer
                ? await ResolveAsync(context, owner, items[i], ct)
                : (null, "you don't have permission to delete (it needs knowledge:write)");
            if (item is not null && !seen.Add((item.Kind, item.ContainerId, item.Target)))
                (item, problem) = (null, "it is already listed earlier in this batch");
            if (item is null)
                problems.Add($"item {i + 1}: {problem}");
            else
                resolved.Add(item);
        }
        if (problems.Count > 0)
            return Fail(DeletionBatchOutcome.Invalid,
                "Nothing was asked for, because some items can't be deleted. Fix or drop them and ask again: " + string.Join("; ", problems) + ".", "items");

        var batch = await batches.CreateAsync(McpApprovalClaims.TokenKey(context.User), owner, resolved, ct);
        await audit.LogAsync("deletion_batch.requested", "deletion_batch", batch.Id.ToString(), new { Items = resolved.Count }, ct);
        return Attempt<DeletionBatchView, DeletionBatchOutcome>.Succeed(DeletionTargets.View(batch, clock.GetUtcNow().UtcDateTime));
    }

    private async Task<(NewDeletionItem? Item, string? Problem)> ResolveAsync(OperationContext context, Guid owner, DeletionRequestItem request, CancellationToken ct)
    {
        if (DeletionTargets.ParseKind(request.Kind) is not DeletionItemKind kind)
            return (null, $"kind must be file, folder or memory_space, not '{request.Kind}'");
        string reason = request.Reason?.Trim() ?? "";
        if (reason.Length == 0)
            return (null, "give a reason, which the owner reads before deciding");
        if (reason.Length > DeletionBatchItemEntity.ReasonMaxLength)
            return (null, $"the reason is longer than {DeletionBatchItemEntity.ReasonMaxLength} characters");
        if (kind == DeletionItemKind.Fact)
            return await ResolveFactAsync(context, owner, request.Target, reason, ct);
        if (string.IsNullOrWhiteSpace(request.Container))
            return (null, "name the container (or memory space) by id or name");

        var found = await getContainer.RunAsync(context, ContainerKey.Parse(request.Container.Trim()), ct);
        if (!found.Succeeded || found.Result is not { } container || !Guid.TryParse(container.Id, out Guid containerId))
            return (null, $"container '{request.Container}' was not found");

        // A memory space deleted as a container is a memory space: the owner sees how many facts go with it.
        if (kind == DeletionItemKind.Container && container.Kind == ContainerKind.Memory)
            kind = DeletionItemKind.MemorySpace;

        switch (kind)
        {
            case DeletionItemKind.Container:
                {
                    var inside = await documents.ListAsync(containerId, take: int.MaxValue, ct: ct);
                    if (inside.Count > 0)
                        return (null, $"container '{container.Name}' still holds {DeletionTargets.Count(inside.Count, "file")}; delete them first");
                    if (await containers.GetAsync(containerId, DeletionTargets.OwnerViewer(owner), ct) is not { } mine)
                        return (null, $"your owner can't see container '{container.Name}', so they can't approve deleting it");
                    return (new NewDeletionItem(kind, containerId, "", $"{mine.Name} (empty container)", reason,
                        DeletionTargets.ContainerFingerprint(mine, inside)), null);
                }

            case DeletionItemKind.MemorySpace:
                {
                    if (container.Kind != ContainerKind.Memory)
                        return (null, $"'{container.Name}' is a documents container, not a memory space");
                    var check = await deleteSpace.RunAsync(context, containerId, dryRun: true, ct);
                    if (!check.Succeeded)
                        return (null, check.Detail ?? "you can't delete this memory space");
                    // The owner deletes it, so the owner must be able to; an agent's own personal space isn't theirs to.
                    if (await containers.GetAsync(containerId, DeletionTargets.OwnerViewer(owner), ct) is not { } space
                        || await spaces.ImpactAsync(containerId, DeletionTargets.OwnerViewer(owner), ct) is not { } impact)
                        return (null, $"your owner can't delete memory space '{container.Name}', so it can't be in a batch; delete it directly");
                    return (new NewDeletionItem(kind, containerId, "", space.Name, reason, DeletionTargets.SpaceFingerprint(space, impact)), null);
                }

            case DeletionItemKind.File:
                {
                    if (container.Kind != ContainerKind.Documents)
                        return (null, $"'{container.Name}' is a memory space, which holds facts, not files");
                    if (string.IsNullOrWhiteSpace(request.Target))
                        return (null, "name the file by its id or path in target");
                    if (await FileRules.FindReadableAsync(documents, access, context, containerId, request.Target.Trim(), ct) is not { } document)
                        return (null, $"file '{request.Target}' was not found in '{container.Name}'");
                    return (new NewDeletionItem(kind, containerId, document.Id, $"{container.Name}:{document.Path ?? document.FileName}", reason,
                        DeletionTargets.FileFingerprint(document)), null);
                }

            default:
                {
                    if (container.Kind != ContainerKind.Documents)
                        return (null, $"'{container.Name}' is a memory space, which has no folders");
                    var (path, error) = FolderRules.Normalize(request.Target);
                    if (path is null)
                        return (null, error);
                    var inside = await documents.ListAsync(containerId, pathPrefix: path, take: int.MaxValue, ct: ct);
                    bool recorded = await folders.ExistsAsync(containerId, path, ct);
                    if (!recorded && inside.Count == 0)
                        return (null, $"folder '{path}' was not found in '{container.Name}'");
                    return (new NewDeletionItem(kind, containerId, path, $"{container.Name}:{path} ({DeletionTargets.Count(inside.Count, "file")})", reason,
                        DeletionTargets.FolderFingerprint(containerId, path, recorded, inside)), null);
                }
        }
    }

    private async Task<(NewDeletionItem? Item, string? Problem)> ResolveFactAsync(
        OperationContext context, Guid owner, string? target, string reason, CancellationToken ct)
    {
        if (!Guid.TryParse(target?.Trim(), out Guid factId))
            return (null, "name the fact by its id in target");
        // Found as the caller first, so a fact the caller can't see reads as missing whoever owns it.
        if (await DeletionTargets.FactAsync(memory, factId, MemoryRules.Caller(context.User), ct) is null)
            return (null, $"fact '{target}' was not found");
        if (await DeletionTargets.FactAsync(memory, factId, DeletionTargets.OwnerViewer(owner), ct) is not var (spaceId, history))
            return (null, $"your owner can't see every version of fact '{target}', so they can't approve deleting it");
        string spaceName = (await containers.GetAsync(spaceId, DeletionTargets.OwnerViewer(owner), ct))?.Name ?? spaceId.ToString();
        return (new NewDeletionItem(DeletionItemKind.Fact, spaceId, factId.ToString(), DeletionTargets.FactLabel(spaceName,
            (history.Versions.FirstOrDefault(v => v.Fact.SupersededAt is null) ?? history.Versions[0]).Fact), reason,
            DeletionTargets.FactFingerprint(factId, history)), null);
    }

    private static Attempt<DeletionBatchView, DeletionBatchOutcome> Fail(DeletionBatchOutcome outcome, string detail, string? field = null) =>
        Attempt<DeletionBatchView, DeletionBatchOutcome>.Fail(outcome, detail, field);
}

/// <summary>
/// Where a batch of deletions stands and what became of each item, optionally waiting while it is pending or running.
/// Only the token that asked can see it; anyone else reads not found.
/// </summary>
[Operation("approvals.batches.status", Scope = "none", Risk = RiskTier.Read)]
public sealed class GetDeletionBatchStatus(IDeletionBatchStore batches, TimeProvider clock)
{
    public async Task<Attempt<DeletionBatchView, DeletionBatchOutcome>> ExecuteAsync(
        OperationContext context, Guid id, TimeSpan wait = default, CancellationToken ct = default)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return NotFound(id);

        string tokenKey = McpApprovalClaims.TokenKey(context.User);
        var max = GetMcpApprovalStatus.MaxWait;
        var deadline = clock.GetUtcNow() + (wait > max ? max : wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
        while (true)
        {
            if (await batches.GetForTokenAsync(id, tokenKey, ct) is not { } batch)
                return NotFound(id);

            var now = clock.GetUtcNow();
            var view = DeletionTargets.View(batch, now.UtcDateTime);
            if (view.State is not ("pending" or "running") || now >= deadline)
                return Attempt<DeletionBatchView, DeletionBatchOutcome>.Succeed(view);

            var left = deadline - now;
            var poll = GetMcpApprovalStatus.PollEvery;
            await Task.Delay(left < poll ? left : poll, clock, ct);
        }
    }

    private static Attempt<DeletionBatchView, DeletionBatchOutcome> NotFound(Guid id) =>
        Attempt<DeletionBatchView, DeletionBatchOutcome>.Fail(DeletionBatchOutcome.NotFound,
            $"Deletion batch {id} was not found. Only the token that asked for a batch can check it.");
}

/// <summary>Batches of deletions waiting for the caller: their own, or everyone's for an administrator, who decides only their own.</summary>
[Operation("approvals.batches.list", Scope = "approvals:decide", Risk = RiskTier.Read)]
public sealed class ListDeletionBatches(IDeletionBatchStore batches, TimeProvider clock)
{
    public async Task<Attempt<IReadOnlyList<DeletionBatchView>, DeletionBatchOutcome>> ExecuteAsync(OperationContext context, CancellationToken ct = default)
    {
        if (context.Surface != OperationSurface.Ui)
            return Attempt<IReadOnlyList<DeletionBatchView>, DeletionBatchOutcome>.Fail(DeletionBatchOutcome.NotFromUi);
        if (Approver.From(context) is not var (userId, isAdmin))
            return Attempt<IReadOnlyList<DeletionBatchView>, DeletionBatchOutcome>.Fail(DeletionBatchOutcome.NotSignedIn);

        var now = clock.GetUtcNow().UtcDateTime;
        var pending = await batches.ListPendingAsync(userId, isAdmin, ct);
        return Attempt<IReadOnlyList<DeletionBatchView>, DeletionBatchOutcome>.Succeed(
            pending.Select(b => DeletionTargets.View(b, now, userId)).ToList());
    }
}

/// <summary>
/// Approves all of a batch, some of it, or none (CON-315). Only its owner may, and only in the UI. On approval
/// Connapse deletes each approved item as the owner, after looking it up again: one that is gone, or changed since
/// the request, is skipped and reported rather than deleted.
/// </summary>
[Operation("approvals.batches.decide", Scope = "approvals:decide", Risk = RiskTier.Destructive)]
public sealed class DecideDeletionBatch(
    IDeletionBatchStore batches,
    IContainerStore containers,
    IDocumentStore documents,
    IDocumentAccess access,
    IFolderStore folders,
    IMemorySpaceStore spaces,
    DeleteFile deleteFile,
    DeleteFolder deleteFolder,
    DeleteMemorySpace deleteSpace,
    IAuditLogger audit,
    TimeProvider clock,
    ILogger<DecideDeletionBatch> logger,
    IMemoryService memory,
    DeleteContainer deleteContainer,
    ForgetFact forgetFact)
{
    /// <param name="approvedItemIds">The items to delete when approving; null approves every item.</param>
    /// <param name="reason">What the owner says about the decision; the agent reads it.</param>
    public async Task<Attempt<DeletionBatchView, DeletionBatchOutcome>> ExecuteAsync(
        OperationContext context, Guid id, bool approve, IReadOnlyCollection<Guid>? approvedItemIds = null, string? reason = null,
        CancellationToken ct = default)
    {
        if (context.Surface != OperationSurface.Ui)
            return Fail(DeletionBatchOutcome.NotFromUi);
        if (Approver.From(context) is not var (userId, _))
            return Fail(DeletionBatchOutcome.NotSignedIn);

        // Someone else's batch reads as not found, so its existence isn't revealed.
        switch (await batches.DecideAsync(id, userId, approve, approvedItemIds, reason, ct))
        {
            case DeletionBatchDecision.Decided:
                break;
            case DeletionBatchDecision.NotPending:
                return Fail(DeletionBatchOutcome.NotPending);
            case DeletionBatchDecision.NothingSelected:
                return Fail(DeletionBatchOutcome.NothingSelected, "Select at least one item to approve, or deny the batch.");
            default:
                return Fail(DeletionBatchOutcome.NotFound);
        }

        await audit.LogAsync(approve ? "deletion_batch.approved" : "deletion_batch.denied", "deletion_batch", id.ToString(),
            new { Items = approvedItemIds?.Count }, CancellationToken.None);

        if (approve && await batches.GetAsync(id, CancellationToken.None) is { } batch)
        {
            // Not the request's token: once approved, a closed page must not leave the batch half done.
            foreach (var item in batch.Items.Where(i => i.State == DeletionItemState.Approved).OrderBy(i => i.Position))
            {
                var (state, outcome) = await RunItemAsync(context, batch.OwnerUserId, item);
                await batches.RecordAsync(item.Id, state, outcome, CancellationToken.None);
            }
            await batches.CompleteAsync(id, CancellationToken.None);
        }

        return await batches.GetAsync(id, CancellationToken.None) is { } done
            ? Attempt<DeletionBatchView, DeletionBatchOutcome>.Succeed(DeletionTargets.View(done, clock.GetUtcNow().UtcDateTime, userId))
            : Fail(DeletionBatchOutcome.NotFound);
    }

    private async Task<(DeletionItemState, string)> RunItemAsync(OperationContext owner, Guid ownerUserId, DeletionBatchItemEntity item)
    {
        CancellationToken ct = CancellationToken.None;
        try
        {
            switch (item.Kind)
            {
                case DeletionItemKind.File:
                    {
                        if (await FileRules.FindReadableAsync(documents, access, owner, item.ContainerId, item.Target, ct) is not { } document)
                            return Gone("file");
                        if (DeletionTargets.FileFingerprint(document) != item.Fingerprint)
                            return Changed("file");
                        var deleted = await deleteFile.RunAsync(owner, item.ContainerId, item.Target, ct);
                        return !deleted.Succeeded ? Failed(deleted.Detail)
                            : deleted.Result!.StorageCleanupFailed ? (DeletionItemState.Deleted, "Deleted; its stored file will be removed on a retry.")
                            : (DeletionItemState.Deleted, "Deleted.");
                    }

                case DeletionItemKind.Folder:
                    {
                        var inside = await documents.ListAsync(item.ContainerId, pathPrefix: item.Target, take: int.MaxValue, ct: ct);
                        bool recorded = await folders.ExistsAsync(item.ContainerId, item.Target, ct);
                        if (!recorded && inside.Count == 0)
                            return Gone("folder");
                        if (DeletionTargets.FolderFingerprint(item.ContainerId, item.Target, recorded, inside) != item.Fingerprint)
                            return Changed("folder");
                        var deleted = await deleteFolder.RunAsync(owner, item.ContainerId, item.Target, ct);
                        return deleted.Succeeded
                            ? (DeletionItemState.Deleted, $"Deleted, with the {DeletionTargets.Count(deleted.Result!.DocumentsDeleted, "file")} in it.")
                            : Failed(deleted.Detail);
                    }

                case DeletionItemKind.Container:
                    {
                        if (await containers.GetAsync(item.ContainerId, DeletionTargets.OwnerViewer(ownerUserId), ct) is not { } container)
                            return Gone("container");
                        var inside = await documents.ListAsync(item.ContainerId, take: int.MaxValue, ct: ct);
                        if (DeletionTargets.ContainerFingerprint(container, inside) != item.Fingerprint)
                            return Changed("container");
                        var deleted = await deleteContainer.RunAsync(owner, ContainerKey.ById(item.ContainerId), ct);
                        return deleted.Succeeded ? (DeletionItemState.Deleted, "Deleted.") : Failed(deleted.Detail);
                    }

                case DeletionItemKind.Fact:
                    {
                        Guid factId = Guid.Parse(item.Target);
                        if (await DeletionTargets.FactAsync(memory, factId, DeletionTargets.OwnerViewer(ownerUserId), ct) is not var (_, history))
                            return Gone("fact");
                        if (DeletionTargets.FactFingerprint(factId, history) != item.Fingerprint)
                            return Changed("fact");
                        var deleted = await forgetFact.RunAsync(owner, null, item.Target, ct);
                        return deleted.Succeeded ? (DeletionItemState.Deleted, "Deleted, with every version.") : Failed(deleted.Detail);
                    }

                default:
                    {
                        MemoryCaller viewer = DeletionTargets.OwnerViewer(ownerUserId);
                        if (await containers.GetAsync(item.ContainerId, viewer, ct) is not { } space)
                            return Gone("memory space");
                        if (await spaces.ImpactAsync(item.ContainerId, viewer, ct) is not { } impact)
                            return Failed("access to the space's facts could not be checked, so it was left alone");
                        if (DeletionTargets.SpaceFingerprint(space, impact) != item.Fingerprint)
                            return Changed("memory space");
                        var deleted = await deleteSpace.RunAsync(owner, item.ContainerId, dryRun: false, ct);
                        return deleted.Succeeded
                            ? (DeletionItemState.Deleted, $"Deleted, with its {DeletionTargets.Count(deleted.Result!.Facts, "fact")}.")
                            : Failed(deleted.Detail);
                    }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting item {ItemId} of an approved batch failed", item.Id);
            return Failed("the delete failed unexpectedly");
        }
    }

    private static (DeletionItemState, string) Gone(string what) =>
        (DeletionItemState.Skipped, $"Skipped: the {what} was already gone when the batch ran.");

    private static (DeletionItemState, string) Changed(string what) =>
        (DeletionItemState.Skipped, $"Skipped: the {what} changed after it was requested, so the owner hadn't seen what would be deleted.");

    private static (DeletionItemState, string) Failed(string? detail) =>
        (DeletionItemState.Failed, $"Not deleted: {detail ?? "the delete was refused"}.");

    private static Attempt<DeletionBatchView, DeletionBatchOutcome> Fail(DeletionBatchOutcome outcome, string? detail = null) =>
        Attempt<DeletionBatchView, DeletionBatchOutcome>.Fail(outcome, detail);
}

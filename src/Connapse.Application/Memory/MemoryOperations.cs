using System.Security.Claims;
using Connapse.Application.Containers;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Application.Memory;

/// <summary>Why a memory read or write didn't succeed.</summary>
public enum MemoryStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "container-not-found", "Container not found")]
    ContainerNotFound,

    [Failure(404, "memory-fact-not-found", "Fact not found")]
    FactNotFound,

    [Failure(400, "invalid-memory", "Invalid memory request")]
    Invalid,

    [Failure(503, "memory-access-unchecked", "Access could not be checked")]
    Unchecked,
}

/// <summary>A fact written, and the one it replaced when it was a correction.</summary>
public sealed record MemoryWritten(MemoryFact Fact, MemoryFact? Replaced);

internal static class MemoryRules
{
    public const string ReadScope = "knowledge:read";
    public const string WriteScope = "knowledge:write";

    /// <summary>
    /// The managed container named by id or name, or null. Only containers resolve, so memory never lands in a synced
    /// source (GitHub, Jira, ...).
    /// </summary>
    public static async Task<Guid?> ContainerAsync(IContainerStore containers, string? idOrName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
            return null;
        Container? found = await ContainerRules.FindAsync(containers, ContainerKey.Parse(idOrName.Trim()), ct);
        return found is not null && Guid.TryParse(found.Id, out Guid id) ? id : null;
    }

    /// <summary>The caller as memory records it, set from the authenticated principal, never from a request.</summary>
    public static MemoryCaller Caller(ClaimsPrincipal user)
    {
        Guid? agent = user.FindFirstValue(SearchPrincipal.ActorTypeClaim) == "agent"
            && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out Guid a) ? a : null;
        return new MemoryCaller(SearchPrincipal.Resolve(user), agent, user.FindFirstValue(ClaimTypes.Name));
    }

    public static Attempt<T, MemoryStatus> Fail<T>(MemoryStatus status, string detail, string? field = null) =>
        Attempt<T, MemoryStatus>.Fail(status, detail.StartsWith("Error: ", StringComparison.Ordinal) ? detail[7..] : detail, field);

    public static Attempt<T, MemoryStatus> WriteForbidden<T>() =>
        Fail<T>(MemoryStatus.Forbidden, "writing memory needs write access (knowledge:write).");

    public static Attempt<T, MemoryStatus> WriteContainerNotFound<T>(string? containerId) =>
        Fail<T>(MemoryStatus.ContainerNotFound, $"Container '{containerId}' not found. Memory lives in containers, not in synced sources.");

    public static Attempt<T, MemoryStatus> FactNotFound<T>(string? factId) =>
        Fail<T>(MemoryStatus.FactNotFound, $"Fact '{factId}' not found.");

    public static Attempt<MemoryWritten, MemoryStatus> Written(MemoryWriteResult result) =>
        result.Error is not { } error
            ? Attempt<MemoryWritten, MemoryStatus>.Succeed(new MemoryWritten(result.Fact!, result.Replaced))
            : Fail<MemoryWritten>(result.Failure switch
            {
                MemoryWriteFailure.NotFound => MemoryStatus.FactNotFound,
                MemoryWriteFailure.Unchecked => MemoryStatus.Unchecked,
                _ => MemoryStatus.Invalid,
            }, error);
}

/// <summary>
/// Stores one fact in a container's memory. The writer must be able to read every source it cites; a fact citing none
/// is seen only by its writer.
/// </summary>
[Operation("memory.remember", Scope = MemoryRules.WriteScope, Risk = RiskTier.Write)]
public sealed class RememberFact(IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<MemoryWritten, MemoryStatus>> RunAsync(
        OperationContext context, string containerId, NewMemoryFact fact, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return MemoryRules.WriteForbidden<MemoryWritten>();
        if (await MemoryRules.ContainerAsync(containers, containerId, ct) is not { } container)
            return MemoryRules.WriteContainerNotFound<MemoryWritten>(containerId);

        return MemoryRules.Written(await memory.RememberAsync(container, fact, MemoryRules.Caller(context.User), ct));
    }
}

/// <summary>
/// Replaces a fact and keeps the old one in history: <see cref="MemoryCorrection.Changed"/> ends it where the new
/// value starts, <see cref="MemoryCorrection.Wrong"/> supersedes it. A fact the caller cannot see reads as missing.
/// </summary>
[Operation("memory.correct", Scope = MemoryRules.WriteScope, Risk = RiskTier.Write)]
public sealed class CorrectFact(IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<MemoryWritten, MemoryStatus>> RunAsync(
        OperationContext context, string containerId, string factId, MemoryCorrection kind, NewMemoryFact replacement,
        CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return MemoryRules.WriteForbidden<MemoryWritten>();
        if (await MemoryRules.ContainerAsync(containers, containerId, ct) is not { } container)
            return MemoryRules.WriteContainerNotFound<MemoryWritten>(containerId);
        if (!Guid.TryParse(factId, out Guid id))
            return MemoryRules.FactNotFound<MemoryWritten>(factId);
        if (!Enum.IsDefined(kind))
            return MemoryRules.Fail<MemoryWritten>(MemoryStatus.Invalid, "kind must be \"changed\" or \"wrong\".", "kind");

        return MemoryRules.Written(await memory.CorrectAsync(container, id, kind, replacement, MemoryRules.Caller(context.User), ct));
    }
}

/// <summary>
/// Deletes a fact for good, with every version of it from corrections. A chain with any version the caller cannot see
/// reads as missing, and nothing is deleted.
/// </summary>
[Operation("memory.forget", Scope = MemoryRules.WriteScope, Risk = RiskTier.Destructive)]
public sealed class ForgetFact(IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer)
{
    /// <returns>How many versions were deleted.</returns>
    public async Task<Attempt<int, MemoryStatus>> RunAsync(OperationContext context, string containerId, string factId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return MemoryRules.WriteForbidden<int>();
        if (await MemoryRules.ContainerAsync(containers, containerId, ct) is not { } container)
            return MemoryRules.WriteContainerNotFound<int>(containerId);
        if (!Guid.TryParse(factId, out Guid id))
            return MemoryRules.FactNotFound<int>(factId);

        (int deleted, string? error) = await memory.ForgetAsync(container, id, MemoryRules.Caller(context.User), ct);
        return error is null ? Attempt<int, MemoryStatus>.Succeed(deleted) : MemoryRules.FactNotFound<int>(factId);
    }
}

/// <summary>
/// Reads facts from a container's memory, each only when the caller can read every source it cites. When access
/// cannot be checked, nothing is shown.
/// </summary>
[Operation("memory.recall", Scope = MemoryRules.ReadScope, Risk = RiskTier.Read)]
public sealed class RecallMemory(IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer)
{
    /// <param name="query">What to read; its container id is replaced by <paramref name="containerId"/>'s.</param>
    public async Task<Attempt<IReadOnlyList<RecalledFact>, MemoryStatus>> RunAsync(
        OperationContext context, string containerId, MemoryQuery query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return MemoryRules.Fail<IReadOnlyList<RecalledFact>>(MemoryStatus.Forbidden, "reading memory needs knowledge:read.");
        if (await MemoryRules.ContainerAsync(containers, containerId, ct) is not { } container)
            return MemoryRules.Fail<IReadOnlyList<RecalledFact>>(MemoryStatus.ContainerNotFound, $"Container '{containerId}' not found.");

        MemoryRecall recall = await memory.RecallAsync(query with { ContainerId = container }, MemoryRules.Caller(context.User), ct);
        return recall.Denied
            ? MemoryRules.Fail<IReadOnlyList<RecalledFact>>(MemoryStatus.Unchecked, "access to memory sources could not be checked; nothing is shown.")
            : Attempt<IReadOnlyList<RecalledFact>, MemoryStatus>.Succeed(recall.Facts);
    }
}

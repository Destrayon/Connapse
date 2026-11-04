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

/// <summary>
/// A fact written, and the one it replaced when it was a correction. <see cref="SavedTo"/> is the space or container
/// a new fact went into, which for an old-style save without sources is the caller's own personal space rather than the
/// container named.
/// </summary>
public sealed record MemoryWritten(MemoryFact Fact, MemoryFact? Replaced, Container? SavedTo = null);

/// <summary>
/// Where a save is aimed. <see cref="IsSpace"/>: named as a space, so the space alone decides personal or team.
/// Otherwise it is an old-style <c>containerId</c>, whose saves without sources stay personal (CON-235).
/// </summary>
public sealed record MemoryTarget(string IdOrName, bool IsSpace)
{
    public static MemoryTarget Container(string idOrName) => new(idOrName, false);

    public static MemoryTarget Space(string idOrName) => new(idOrName, true);
}

internal static class MemoryRules
{
    public const string ReadScope = "knowledge:read";
    public const string WriteScope = "knowledge:write";

    /// <summary>The personal space every writer has, made the first time it is named.</summary>
    public const string MyMemory = "my-memory";

    /// <summary>
    /// The managed container or memory space named by id or name, as <paramref name="viewer"/> may see it, or null.
    /// Only containers resolve, so memory never lands in a synced source (GitHub, Jira, ...); someone else's personal
    /// space reads as missing. A name both a team container and one of the viewer's personal spaces use is refused,
    /// with both ids, since personal names are only unique per owner.
    /// </summary>
    public static async Task<(Container? Found, string? Ambiguous)> SpaceAsync(
        IContainerStore containers, string? idOrName, MemoryCaller viewer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
            return (null, null);
        ContainerKey key = ContainerKey.Parse(idOrName.Trim());
        if (key.Id is Guid id)
            return (await containers.GetAsync(id, viewer, ct), null);
        IReadOnlyList<Container> named = await containers.FindByNameAsync(key.Name!.ToLowerInvariant(), viewer, ct);
        return named.Count > 1
            ? (null, $"'{named[0].Name}' names both a team space or container ({named[0].Id}) and your personal space "
                + $"({named[1].Id}). Name the one you mean by id.")
            : (named.FirstOrDefault(), null);
    }

    /// <summary>
    /// The id of the container or space named, as <see cref="SpaceAsync"/> resolves it, or the refusal when the name
    /// is ambiguous. Both null: not found.
    /// </summary>
    public static async Task<(Guid? Id, string? Ambiguous)> ContainerAsync(
        IContainerStore containers, string? idOrName, MemoryCaller viewer, CancellationToken ct)
    {
        (Container? found, string? ambiguous) = await SpaceAsync(containers, idOrName, viewer, ct);
        return (found is not null && Guid.TryParse(found.Id, out Guid id) ? id : null, ambiguous);
    }

    /// <summary>
    /// The personal space an old-style save without sources goes to: the container's name with <c>-personal</c>, cut
    /// so it stays a valid container name.
    /// </summary>
    public static string PersonalSpaceFor(string containerName)
    {
        const string suffix = "-personal";
        string stem = containerName.Length > 128 - suffix.Length ? containerName[..(128 - suffix.Length)].TrimEnd('-') : containerName;
        return stem + suffix;
    }

    /// <summary>Whether a personal space can be made for <paramref name="caller"/>: it needs a user or an agent to own it.</summary>
    public static bool CanOwn(MemoryCaller caller) => caller.UserId is not null || caller.AgentId is not null;

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

    /// <summary>
    /// Whether the caller may save memory, personal or team: write access (knowledge:write). Both checks are needed:
    /// the writer role alone would pass an Editor's read-only token. Read-only callers only recall, since every saved
    /// fact costs storage and embedding compute.
    /// </summary>
    public static async Task<bool> WriterAsync(IOperationAuthorizer authorizer, OperationContext context) =>
        await authorizer.AllowsAsync(context.User, Policies.Writer) && await authorizer.AllowsAsync(context.User, Policies.KnowledgeWrite);

    public static Attempt<T, MemoryStatus> WriteContainerNotFound<T>(string? containerId) =>
        Fail<T>(MemoryStatus.ContainerNotFound, $"Container '{containerId}' not found. Memory lives in containers, not in synced sources.");

    public static Attempt<T, MemoryStatus> FactNotFound<T>(string? factId) =>
        Fail<T>(MemoryStatus.FactNotFound, $"Fact '{factId}' not found.");

    public static Attempt<MemoryWritten, MemoryStatus> Written(MemoryWriteResult result, Container? savedTo = null) =>
        result.Error is not { } error
            ? Attempt<MemoryWritten, MemoryStatus>.Succeed(new MemoryWritten(result.Fact!, result.Replaced, savedTo))
            : Fail<MemoryWritten>(result.Failure switch
            {
                MemoryWriteFailure.NotFound => MemoryStatus.FactNotFound,
                MemoryWriteFailure.Unchecked => MemoryStatus.Unchecked,
                MemoryWriteFailure.Forbidden => MemoryStatus.Forbidden,
                _ => MemoryStatus.Invalid,
            }, error);
}

/// <summary>
/// Stores one fact in a memory space or container, personal or team. Named as a space, the space decides: a personal
/// space's facts are personal and everything else's team, and asking for the other is refused. Named the old way, by
/// <c>containerId</c>, a fact citing no sources is personal and goes to the caller's <c>&lt;container&gt;-personal</c>
/// space, so an old client never shares a fact by accident. The writer must be able to read every source it cites.
/// Either scope needs write access; read-only callers can only recall.
/// </summary>
[Operation("memory.remember", Scope = MemoryRules.WriteScope, Risk = RiskTier.Write)]
public sealed class RememberFact(IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer)
{
    public Task<Attempt<MemoryWritten, MemoryStatus>> RunAsync(
        OperationContext context, string containerId, NewMemoryFact fact, CancellationToken ct = default) =>
        RunAsync(context, MemoryTarget.Container(containerId), fact, ct);

    public async Task<Attempt<MemoryWritten, MemoryStatus>> RunAsync(
        OperationContext context, MemoryTarget target, NewMemoryFact fact, CancellationToken ct = default)
    {
        static Attempt<MemoryWritten, MemoryStatus> Invalid(string detail, string field) =>
            MemoryRules.Fail<MemoryWritten>(MemoryStatus.Invalid, detail, field);

        if (!await MemoryRules.WriterAsync(authorizer, context))
            return MemoryRules.WriteForbidden<MemoryWritten>();
        if (fact.Scope is { } asked && !Enum.IsDefined(asked))
            return Invalid("scope must be \"personal\" or \"team\".", "scope");

        MemoryCaller caller = MemoryRules.Caller(context.User);
        string field = target.IsSpace ? "space" : "containerId";
        (Container? named, string? ambiguous) = await MemoryRules.SpaceAsync(containers, target.IdOrName, caller, ct);
        if (ambiguous is not null)
            return Invalid(ambiguous, field);
        if (named is null && target.IsSpace && string.Equals(target.IdOrName?.Trim(), MemoryRules.MyMemory, StringComparison.OrdinalIgnoreCase))
        {
            if (!MemoryRules.CanOwn(caller))
                return MemoryRules.Fail<MemoryWritten>(MemoryStatus.Forbidden, "personal memory needs a signed-in user or agent to belong to.");
            named = await containers.EnsurePersonalSpaceAsync(caller, MemoryRules.MyMemory, "Your own memory: only you see what is saved here.", ct);
        }
        if (named is null)
            return MemoryRules.WriteContainerNotFound<MemoryWritten>(target.IdOrName);

        bool cites = fact.Sources is { Count: > 0 };
        MemoryScope scope = target.IsSpace ? named.Audience
            : fact.Scope ?? (named.Audience == MemoryScope.Personal || !cites ? MemoryScope.Personal : MemoryScope.Team);
        if (fact.Scope is { } requested && requested != scope)
            return requested == MemoryScope.Personal
                ? Invalid(await PersonalRefusedAsync(named, caller, ct), "scope")
                : Invalid($"'{named.Name}' is a personal space, so only you see what is saved there. Save team memory to a team space or container.", "scope");
        if (scope == MemoryScope.Team && named.Audience == MemoryScope.Personal)
            return Invalid($"'{named.Name}' is a personal space, so only you see what is saved there. Save team memory to a team space or container.", "scope");
        if (scope == MemoryScope.Team && !cites && !target.IsSpace)
            return Invalid("saving team memory that cites no sources needs `space` naming a team space or container, so sharing it is a choice.", "scope");

        Container saveTo = named;
        if (scope == MemoryScope.Personal && named.Audience == MemoryScope.Team)
        {
            // Only an old-style save gets here: the container named is team, so the fact goes to the caller's own space for it.
            if (!MemoryRules.CanOwn(caller))
                return MemoryRules.Fail<MemoryWritten>(MemoryStatus.Forbidden, "personal memory needs a signed-in user or agent to belong to.");
            saveTo = await containers.EnsurePersonalSpaceAsync(
                caller, MemoryRules.PersonalSpaceFor(named.Name), $"Your personal memory saved to {named.Name}.", ct);
        }

        return MemoryRules.Written(await memory.RememberAsync(Guid.Parse(saveTo.Id), fact with { Scope = scope }, caller, ct), saveTo);
    }

    /// <summary>Why personal memory can't go in a team space or container, and which of the caller's own spaces it can go in.</summary>
    private async Task<string> PersonalRefusedAsync(Container named, MemoryCaller caller, CancellationToken ct)
    {
        string what = named.Kind == ContainerKind.Memory ? "a team space" : "a document container, whose memory is team memory";
        List<string> own = (await containers.ListAsync(0, ListContainers.MaxTake, caller, ct))
            .Where(c => c.Audience == MemoryScope.Personal).Select(c => c.Name).ToList();
        string where = own.Count > 0
            ? $"Save it to one of your personal spaces: {string.Join(", ", own)}."
            : $"You have no personal space yet: save it to space \"{MemoryRules.MyMemory}\" and it is made for you.";
        return $"'{named.Name}' is {what}, so everyone can see what is saved there. {where}";
    }
}

/// <summary>
/// Replaces a fact and keeps the old one in history: <see cref="MemoryCorrection.Changed"/> ends it where the new
/// value starts, <see cref="MemoryCorrection.Wrong"/> supersedes it. A fact the caller cannot see reads as missing.
/// The new version keeps the fact's scope and stays in the fact's space. Correcting needs write access, whatever the scope.
/// </summary>
[Operation("memory.correct", Scope = MemoryRules.WriteScope, Risk = RiskTier.Write)]
public sealed class CorrectFact(IMemoryService memory, IOperationAuthorizer authorizer)
{
    /// <param name="containerId">
    /// Ignored: the fact is found by id in every space and container the caller can see, so a fact moved into a space
    /// (CON-236) is still reached through the container it used to be in.
    /// </param>
    public async Task<Attempt<MemoryWritten, MemoryStatus>> RunAsync(
        OperationContext context, string? containerId, string factId, MemoryCorrection kind, NewMemoryFact replacement,
        CancellationToken ct = default)
    {
        if (!await MemoryRules.WriterAsync(authorizer, context))
            return MemoryRules.WriteForbidden<MemoryWritten>();
        // The answer holds the replaced fact, so a correction is a read as well.
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return MemoryRules.Fail<MemoryWritten>(MemoryStatus.Forbidden, "correcting memory also needs knowledge:read: the answer shows the old fact.");
        MemoryCaller caller = MemoryRules.Caller(context.User);
        if (!Guid.TryParse(factId, out Guid id))
            return MemoryRules.FactNotFound<MemoryWritten>(factId);
        if (!Enum.IsDefined(kind))
            return MemoryRules.Fail<MemoryWritten>(MemoryStatus.Invalid, "kind must be \"changed\" or \"wrong\".", "kind");

        return MemoryRules.Written(await memory.CorrectAsync(id, kind, replacement, caller, teamWriter: true, ct));
    }
}

/// <summary>
/// Deletes a fact for good, with every version of it from corrections. A chain with any version the caller cannot see
/// reads as missing, and nothing is deleted. Forgetting needs write access, whatever the scope.
/// </summary>
[Operation("memory.forget", Scope = MemoryRules.WriteScope, Risk = RiskTier.Destructive)]
public sealed class ForgetFact(IMemoryService memory, IOperationAuthorizer authorizer)
{
    /// <param name="containerId">
    /// Ignored: the fact is found by id in every space and container the caller can see, so a fact moved into a space
    /// (CON-236) is still reached through the container it used to be in.
    /// </param>
    /// <returns>How many versions were deleted.</returns>
    public async Task<Attempt<int, MemoryStatus>> RunAsync(OperationContext context, string? containerId, string factId, CancellationToken ct = default)
    {
        if (!await MemoryRules.WriterAsync(authorizer, context))
            return MemoryRules.WriteForbidden<int>();
        MemoryCaller caller = MemoryRules.Caller(context.User);
        if (!Guid.TryParse(factId, out Guid id))
            return MemoryRules.FactNotFound<int>(factId);

        (int deleted, MemoryWriteFailure failure) = await memory.ForgetAsync(id, caller, teamWriter: true, ct);
        return failure switch
        {
            MemoryWriteFailure.None => Attempt<int, MemoryStatus>.Succeed(deleted),
            MemoryWriteFailure.Forbidden => MemoryRules.Fail<int>(MemoryStatus.Forbidden, "deleting a team fact needs write access (knowledge:write)."),
            MemoryWriteFailure.Unchecked => MemoryRules.Fail<int>(MemoryStatus.Unchecked, "access to memory sources could not be checked; nothing was deleted."),
            _ => MemoryRules.FactNotFound<int>(factId),
        };
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
        MemoryCaller caller = MemoryRules.Caller(context.User);
        (Guid? found, string? ambiguous) = await MemoryRules.ContainerAsync(containers, containerId, caller, ct);
        if (ambiguous is not null)
            return MemoryRules.Fail<IReadOnlyList<RecalledFact>>(MemoryStatus.Invalid, ambiguous, "containerId");
        if (found is not { } container)
            return MemoryRules.Fail<IReadOnlyList<RecalledFact>>(MemoryStatus.ContainerNotFound, $"Container '{containerId}' not found.");

        MemoryRecall recall = await memory.RecallAsync(query with { ContainerId = container }, caller, ct);
        return recall.Denied
            ? MemoryRules.Fail<IReadOnlyList<RecalledFact>>(MemoryStatus.Unchecked, "access to memory sources could not be checked; nothing is shown.")
            : Attempt<IReadOnlyList<RecalledFact>, MemoryStatus>.Succeed(recall.Facts);
    }
}

/// <summary>Every memory operation in one place, for the container page's Memory tab.</summary>
public sealed class MemoryOperations(RememberFact remember, CorrectFact correct, ForgetFact forget, RecallMemory recall, ListMemoryHistory history)
{
    public ListMemoryHistory History { get; } = history;

    public RememberFact Remember { get; } = remember;

    public CorrectFact Correct { get; } = correct;

    public ForgetFact Forget { get; } = forget;

    public RecallMemory Recall { get; } = recall;
}

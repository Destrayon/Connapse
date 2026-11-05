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

    [Failure(409, "memory-fact-exists", "A current fact already says this")]
    Exists,
}

/// <summary>
/// A fact written, and the one it replaced when it was a correction. <see cref="SavedTo"/> is the space a new fact went
/// into, which for an old-style save without sources is the caller's own personal space rather than the space named.
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
    /// The memory space named by id or name, as <paramref name="viewer"/> may see it, or null. Only memory containers
    /// resolve: naming a document container is refused, and someone else's personal space reads as missing. A name a
    /// team space and one of the viewer's personal spaces both use is refused, with both ids, since personal names are
    /// only unique per owner; a document container sharing a space's name doesn't count.
    /// </summary>
    public static async Task<(Container? Found, string? Refused)> SpaceAsync(
        IContainerStore containers, string? idOrName, MemoryCaller viewer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
            return (null, null);
        ContainerKey key = ContainerKey.Parse(idOrName.Trim());
        if (key.Id is Guid id)
        {
            Container? found = await containers.GetAsync(id, viewer, ct);
            return found is null || found.Kind == ContainerKind.Memory ? (found, null) : (null, NotASpace(found));
        }
        IReadOnlyList<Container> named = await containers.FindByNameAsync(key.Name!.ToLowerInvariant(), viewer, ct);
        List<Container> matches = [.. named.Where(c => c.Kind == ContainerKind.Memory)];
        if (matches.Count > 1)
            return (null, $"'{matches[0].Name}' names both a team space ({matches[0].Id}) and your personal space "
                + $"({matches[1].Id}). Name the one you mean by id.");
        return matches.Count == 0 && named.Count > 0 ? (null, NotASpace(named[0])) : (matches.FirstOrDefault(), null);
    }

    private static string NotASpace(Container container) =>
        $"'{container.Name}' is a document container, and those don't hold memory. Use a personal or team space.";
    /// <summary>
    /// The id of the space named, as <see cref="SpaceAsync"/> resolves it, or the refusal. Both null: not found.
    /// </summary>
    public static async Task<(Guid? Id, string? Refused)> ContainerAsync(
        IContainerStore containers, string? idOrName, MemoryCaller viewer, CancellationToken ct)
    {
        (Container? found, string? refused) = await SpaceAsync(containers, idOrName, viewer, ct);
        return (found is not null && Guid.TryParse(found.Id, out Guid id) ? id : null, refused);
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
        Fail<T>(MemoryStatus.ContainerNotFound, $"Space '{containerId}' not found. Memory lives in personal and team spaces.");

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
/// Stores one fact in a memory space, personal or team. Named as a space, the space decides: a personal space's facts
/// are personal and a team space's team, and asking for the other is refused. Named the old way, by
/// <c>containerId</c>, a fact citing no sources is personal and goes to the caller's <c>&lt;space&gt;-personal</c>
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
        // my-memory always means the caller's own space, even when a team space or container has that name, since
        // the tools offer it as the private place to save; the team one is still reachable by id.
        bool myMemory = target.IsSpace && string.Equals(target.IdOrName?.Trim(), MemoryRules.MyMemory, StringComparison.OrdinalIgnoreCase);
        (Container? named, string? refused) = myMemory
            ? (null, null)
            : await MemoryRules.SpaceAsync(containers, target.IdOrName, caller, ct);
        if (refused is not null)
            return Invalid(refused, field);
        if (myMemory)
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
                : Invalid($"'{named.Name}' is a personal space, so only you see what is saved there. Save team memory to a team space.", "scope");
        if (scope == MemoryScope.Team && named.Audience == MemoryScope.Personal)
            return Invalid($"'{named.Name}' is a personal space, so only you see what is saved there. Save team memory to a team space.", "scope");
        if (scope == MemoryScope.Team && !cites && !target.IsSpace)
            return Invalid("saving team memory that cites no sources needs `space` naming a team space, so sharing it is a choice.", "scope");

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

    /// <summary>Why personal memory can't go in a team space, and which of the caller's own spaces it can go in.</summary>
    private async Task<string> PersonalRefusedAsync(Container named, MemoryCaller caller, CancellationToken ct)
    {
        List<string> own = (await containers.ListAsync(0, ListContainers.MaxTake, caller, ct))
            .Where(c => c.Audience == MemoryScope.Personal).Select(c => c.Name).ToList();
        string where = own.Count > 0
            ? $"Save it to one of your personal spaces: {string.Join(", ", own)}."
            : $"You have no personal space yet: save it to space \"{MemoryRules.MyMemory}\" and it is made for you.";
        return $"'{named.Name}' is a team space, so everyone can see what is saved there. {where}";
    }
}

/// <summary>
/// Replaces a fact and keeps the old one in history: <see cref="MemoryCorrection.Changed"/> ends it where the new
/// value starts, <see cref="MemoryCorrection.Wrong"/> supersedes it. A fact the caller cannot see reads as missing.
/// The new version keeps the fact's scope and stays in the fact's space. Correcting needs write access, whatever the scope.
/// A fact left in a document container reads as missing too, since those don't hold memory.
/// </summary>
[Operation("memory.correct", Scope = MemoryRules.WriteScope, Risk = RiskTier.Write)]
public sealed class CorrectFact(IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer)
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
        if (await memory.SpaceOfAsync(id, ct) is { } spaceId
            && await containers.GetAsync(spaceId, caller, ct) is { Kind: not ContainerKind.Memory })
            return MemoryRules.FactNotFound<MemoryWritten>(factId);

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
/// Reads facts from a memory space, each only when the caller can read every source it cites. When access
/// cannot be checked, nothing is shown.
/// </summary>
[Operation("memory.recall", Scope = MemoryRules.ReadScope, Risk = RiskTier.Read)]
public sealed class RecallMemory(
    IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer, IMemorySpaceStore? spaces = null)
{
    /// <summary>
    /// The most spaces one recall reads: an unnamed one reads the most recently changed first and counts the rest as
    /// skipped; a recall naming more is refused.
    /// </summary>
    public const int MaxSpaces = 100;

    /// <summary>
    /// Reads one space, named by id or name, or, when <paramref name="space"/> is blank, every space the caller may
    /// see memory in. See the overload naming several.
    /// </summary>
    public Task<Attempt<SpacedRecall, MemoryStatus>> SpacesAsync(
        OperationContext context, string? space, MemoryQuery query, CancellationToken ct = default) =>
        AcrossAsync(context, string.IsNullOrWhiteSpace(space) ? [] : [space], query, ct);

    /// <summary>
    /// Reads the spaces in <paramref name="named"/> (ids or names) as one memory, or, when it names none, every space
    /// the caller may see memory in. Spaces are returned with the facts so each can be labelled. The facts of every
    /// space read are ranked together in one read, each space's in the order a recall of that space alone gives them,
    /// and each passes the same visibility rule as a one-space recall, so reading more spaces never shows a fact the
    /// caller couldn't recall from its own space. An unnamed read skips spaces with no current facts unless it asks
    /// for history, a point in time, a change window or one chain, since those can find facts no longer current.
    /// </summary>
    public async Task<Attempt<SpacedRecall, MemoryStatus>> AcrossAsync(
        OperationContext context, IReadOnlyList<string> named, MemoryQuery query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Forbidden, "reading memory needs knowledge:read.");
        MemoryCaller caller = MemoryRules.Caller(context.User);

        Dictionary<Guid, Container> labels = [];
        if (named.Count > 0)
        {
            string field = named.Count == 1 ? "space" : "spaces";
            if (named.Count > MaxSpaces)
                return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Invalid, $"name at most {MaxSpaces} spaces, or none to read every space you can see.", field);
            foreach (string key in named)
            {
                (Container? found, string? refused) = await MemoryRules.SpaceAsync(containers, key, caller, ct);
                if (refused is not null)
                    return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Invalid, refused, field);
                if (found is null || !Guid.TryParse(found.Id, out Guid id))
                    return MemoryRules.Fail<SpacedRecall>(MemoryStatus.ContainerNotFound, $"Space '{key}' not found. memory_spaces lists the ones you can use.");
                labels[id] = found;
            }
            return await ReadAsync(query, labels, 0, caller, ct);
        }

        if (spaces is null)
            return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Invalid, "name the space to read.", "space");
        bool beyondCurrent = query.History || query.AsOf is not null || query.ChangedFrom is not null
            || query.ChangedTo is not null || query.ChainId is not null;
        IReadOnlyList<MemorySpaceSummary>? listed = await spaces.ListAsync(caller, null, ct);
        if (listed is null)
            return Unchecked();

        List<MemorySpaceSummary> read = [.. listed
            .Where(s => beyondCurrent || s.Facts > 0)
            .OrderByDescending(s => s.UpdatedAt)
            .ThenBy(s => s.Container.Id, StringComparer.Ordinal)];
        int skipped = Math.Max(0, read.Count - MaxSpaces);
        foreach (MemorySpaceSummary summary in read.Take(MaxSpaces))
        {
            if (Guid.TryParse(summary.Container.Id, out Guid id))
                labels[id] = summary.Container;
        }
        return labels.Count == 0
            ? Attempt<SpacedRecall, MemoryStatus>.Succeed(new SpacedRecall([], labels, skipped))
            : await ReadAsync(query, labels, skipped, caller, ct);
    }

    /// <summary>One recall over every space in <paramref name="labels"/>, read from one snapshot.</summary>
    private async Task<Attempt<SpacedRecall, MemoryStatus>> ReadAsync(
        MemoryQuery query, Dictionary<Guid, Container> labels, int skipped, MemoryCaller caller, CancellationToken ct)
    {
        Guid[] ids = [.. labels.Keys];
        MemoryRecall recall = await memory.RecallAsync(
            query with { ContainerId = ids[0], ContainerIds = ids.Length > 1 ? ids : null }, caller, ct);
        return recall.Denied
            ? Unchecked()
            : Attempt<SpacedRecall, MemoryStatus>.Succeed(new SpacedRecall(recall.Facts, labels, skipped));
    }

    /// <summary>
    /// A page of one named space's facts the caller can see, with a cursor for the next page, however deep. Each page
    /// is one keyset read, so the thousandth page costs what the first does. A cursor from another space, filter or
    /// sort is refused.
    /// </summary>
    public async Task<Attempt<SpacedRecall, MemoryStatus>> PageAsync(
        OperationContext context, string space, MemoryFactListing listing, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Forbidden, "reading memory needs knowledge:read.");
        MemoryCaller caller = MemoryRules.Caller(context.User);
        (Container? named, string? refused) = await MemoryRules.SpaceAsync(containers, space, caller, ct);
        if (refused is not null)
            return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Invalid, refused, "space");
        if (named is null || !Guid.TryParse(named.Id, out Guid id))
            return MemoryRules.Fail<SpacedRecall>(MemoryStatus.ContainerNotFound, $"Space '{space}' not found. memory_spaces lists the ones you can use.");
        if (MemoryPaging.Refusal(listing.Filter) is { } refusal)
            return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Invalid, refusal.Detail, refusal.Field);
        if (!Enum.IsDefined(listing.Sort))
            return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Invalid, "sort is not one this read takes.", "sort");
        if (MemoryPaging.Limit(listing.Limit, MemoryPaging.DefaultLimit) is not { } limit)
            return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Invalid, $"limit must be 1 to {MemoryPaging.MaxLimit}.", "limit");
        MemoryFactSort sort = listing.Sort == MemoryFactSort.Relevance && string.IsNullOrWhiteSpace(listing.Filter.Text)
            ? MemoryFactSort.Recorded
            : listing.Sort;

        string fingerprint = MemoryCursor.Fingerprint([.. MemoryPaging.Parts(id, listing.Filter), sort]);
        if (!MemoryCursor.TryDecode(listing.Cursor, fingerprint, out MemoryFactKey? after))
            return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Invalid, MemoryPaging.BadCursor, "cursor");

        MemoryFactPage page = await memory.ListAsync(id, listing.Filter, sort, after, limit, caller, ct);
        return page.Denied
            ? Unchecked()
            : Attempt<SpacedRecall, MemoryStatus>.Succeed(new SpacedRecall(page.Facts, new Dictionary<Guid, Container> { [id] = named }, 0,
                page.Next is { } next ? MemoryCursor.Encode(next, fingerprint) : null));
    }

    private static Attempt<SpacedRecall, MemoryStatus> Unchecked() =>
        MemoryRules.Fail<SpacedRecall>(MemoryStatus.Unchecked, "access to memory sources could not be checked; nothing is shown.");

    /// <param name="query">What to read; its container id is replaced by <paramref name="containerId"/>'s.</param>
    public async Task<Attempt<IReadOnlyList<RecalledFact>, MemoryStatus>> RunAsync(
        OperationContext context, string containerId, MemoryQuery query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return MemoryRules.Fail<IReadOnlyList<RecalledFact>>(MemoryStatus.Forbidden, "reading memory needs knowledge:read.");
        MemoryCaller caller = MemoryRules.Caller(context.User);
        (Guid? found, string? refused) = await MemoryRules.ContainerAsync(containers, containerId, caller, ct);
        if (refused is not null)
            return MemoryRules.Fail<IReadOnlyList<RecalledFact>>(MemoryStatus.Invalid, refused, "containerId");
        if (found is not { } container)
            return MemoryRules.Fail<IReadOnlyList<RecalledFact>>(MemoryStatus.ContainerNotFound, $"Space '{containerId}' not found.");

        MemoryRecall recall = await memory.RecallAsync(query with { ContainerId = container, ContainerIds = null }, caller, ct);
        return recall.Denied
            ? MemoryRules.Fail<IReadOnlyList<RecalledFact>>(MemoryStatus.Unchecked, "access to memory sources could not be checked; nothing is shown.")
            : Attempt<IReadOnlyList<RecalledFact>, MemoryStatus>.Succeed(recall.Facts);
    }
}

/// <summary>
/// Facts read from one or more spaces, with the spaces they came from for labelling, and how many spaces an unnamed
/// read left out for being past <see cref="RecallMemory.MaxSpaces"/>. <paramref name="NextCursor"/> continues a paged
/// read of one space, and is null on its last page and for every other read.
/// </summary>
public sealed record SpacedRecall(
    IReadOnlyList<RecalledFact> Facts, IReadOnlyDictionary<Guid, Container> Spaces, int SkippedSpaces, string? NextCursor = null);

/// <summary>Every memory operation in one place, for the container page's Memory tab.</summary>
public sealed class MemoryOperations(
    RememberFact remember, CorrectFact correct, ForgetFact forget, RecallMemory recall, ListMemoryHistory history,
    ListMemoryFacts list, CountMemoryFacts count, ListMemorySubjects subjects)
{
    public ListMemoryFacts List { get; } = list;

    public CountMemoryFacts Count { get; } = count;

    public ListMemorySubjects Subjects { get; } = subjects;

    public ListMemoryHistory History { get; } = history;

    public RememberFact Remember { get; } = remember;

    public CorrectFact Correct { get; } = correct;

    public ForgetFact Forget { get; } = forget;

    public RecallMemory Recall { get; } = recall;
}

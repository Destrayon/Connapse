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
/// <see cref="Confirmed"/>: the write confirmed <see cref="Fact"/>, which memory already held, instead of adding one.
/// </summary>
public sealed record MemoryWritten(MemoryFact Fact, MemoryFact? Replaced, Container? SavedTo = null, bool Confirmed = false);

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
        Fail<T>(MemoryStatus.ContainerNotFound, $"Space '{containerId}' not found. Memory lives in personal and team spaces: "
            + "pick one you have, or create one, then save to it. No space is made for you.");

    public static Attempt<T, MemoryStatus> FactNotFound<T>(string? factId) =>
        Fail<T>(MemoryStatus.FactNotFound, $"Fact '{factId}' not found.");

    public static Attempt<MemoryWritten, MemoryStatus> Written(MemoryWriteResult result, Container? savedTo = null) =>
        result.Error is not { } error
            ? Attempt<MemoryWritten, MemoryStatus>.Succeed(new MemoryWritten(result.Fact!, result.Replaced, savedTo, result.Confirmed))
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
        // No space is made by naming it (CON-291): a save names a space that exists, or is told to pick or create one.
        (Container? named, string? refused) = await MemoryRules.SpaceAsync(containers, target.IdOrName, caller, ct);
        if (refused is not null)
            return Invalid(refused, field);
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
            : "You have no personal space yet: create one, then save it there.";
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
/// Says a fact is still true after checking its real source: records when, and moves its check-by date on by the
/// period asked for or, without one, by the fact's own (CON-340). The fact keeps its id, value and dates; a fact that
/// is no longer true is corrected instead. A fact the caller cannot see reads as missing. Needs write access, and read
/// access, since the answer shows the fact.
/// </summary>
[Operation("memory.confirm", Scope = MemoryRules.WriteScope, Risk = RiskTier.Write)]
public sealed class ConfirmFact(IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<MemoryWritten, MemoryStatus>> RunAsync(
        OperationContext context, string factId, MemoryCheckBy? check, CancellationToken ct = default)
    {
        if (!await MemoryRules.WriterAsync(authorizer, context))
            return MemoryRules.WriteForbidden<MemoryWritten>();
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return MemoryRules.Fail<MemoryWritten>(MemoryStatus.Forbidden, "confirming memory also needs knowledge:read: the answer shows the fact.");
        MemoryCaller caller = MemoryRules.Caller(context.User);
        if (!Guid.TryParse(factId, out Guid id))
            return MemoryRules.FactNotFound<MemoryWritten>(factId);
        if (await memory.SpaceOfAsync(id, ct) is { } spaceId
            && await containers.GetAsync(spaceId, caller, ct) is { Kind: not ContainerKind.Memory })
            return MemoryRules.FactNotFound<MemoryWritten>(factId);

        return MemoryRules.Written(await memory.ConfirmAsync(id, check, caller, teamWriter: true, ct));
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
    /// <summary>The most spaces one unnamed recall reads, the most recently changed first; the rest are counted as skipped.</summary>
    public const int MaxSpaces = 100;

    /// <summary>
    /// Reads one space, named by id or name, or, when <paramref name="space"/> is blank, every space the caller may
    /// see memory in, merged: by rank when the query has text, newest first otherwise. Spaces are returned with the
    /// facts so each can be labelled. An unnamed read skips spaces with no current facts unless it asks for history,
    /// a point in time, a change window or one chain, since those can find facts no longer current.
    /// </summary>
    public async Task<Attempt<SpacedRecall, MemoryStatus>> SpacesAsync(
        OperationContext context, string? space, MemoryQuery query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Forbidden, "reading memory needs knowledge:read.");
        MemoryCaller caller = MemoryRules.Caller(context.User);

        if (!string.IsNullOrWhiteSpace(space))
        {
            (Container? named, string? refused) = await MemoryRules.SpaceAsync(containers, space, caller, ct);
            if (refused is not null)
                return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Invalid, refused, "space");
            if (named is null || !Guid.TryParse(named.Id, out Guid id))
                return MemoryRules.Fail<SpacedRecall>(MemoryStatus.ContainerNotFound, $"Space '{space}' not found. memory_spaces lists the ones you can use.");
            MemoryRecall one = await memory.RecallAsync(query with { ContainerId = id }, caller, ct);
            return one.Denied
                ? Unchecked()
                : Attempt<SpacedRecall, MemoryStatus>.Succeed(new SpacedRecall(one.Facts, new Dictionary<Guid, Container> { [id] = named }, 0));
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
        Dictionary<Guid, Container> labels = [];
        List<IReadOnlyList<RecalledFact>> found = [];
        foreach (MemorySpaceSummary summary in read.Take(MaxSpaces))
        {
            if (!Guid.TryParse(summary.Container.Id, out Guid id))
                continue;
            MemoryRecall each = await memory.RecallAsync(query with { ContainerId = id }, caller, ct);
            if (each.Denied)
                return Unchecked();
            labels[id] = summary.Container;
            found.Add(each.Facts);
        }
        return Attempt<SpacedRecall, MemoryStatus>.Succeed(
            new SpacedRecall(SpacedRecall.Merge(found, !string.IsNullOrWhiteSpace(query.Text), query.Limit), labels, skipped));
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

    /// <summary>
    /// A recall as the API and the memory_recall tool both take it. One named space without a point in time or an
    /// attribute is read a page at a time (by relevance when there is text, newest recorded first otherwise); any
    /// other read goes through <see cref="SpacesAsync"/>, and a cursor on it is refused, since it has no next page.
    /// </summary>
    public Task<Attempt<SpacedRecall, MemoryStatus>> RecallAsync(
        OperationContext context, MemoryRecallRequest request, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(request.Space) && request.AsOf is null && string.IsNullOrWhiteSpace(request.Attribute))
        {
            MemoryFactFilter filter = new(Text: request.Text, Subject: request.Subject,
                Status: request.History ? MemoryFactStatus.All : MemoryFactStatus.Current, Check: request.Check);
            MemoryFactSort sort = string.IsNullOrWhiteSpace(request.Text) ? MemoryFactSort.Recorded : MemoryFactSort.Relevance;
            return PageAsync(context, request.Space,
                new MemoryFactListing(filter, sort, request.Cursor, Math.Clamp(request.Limit, 1, MemoryPaging.MaxLimit)), ct);
        }
        if (!string.IsNullOrWhiteSpace(request.Cursor))
            return Task.FromResult(MemoryRules.Fail<SpacedRecall>(MemoryStatus.Invalid,
                "cursor works only when you name a `space` and leave out `asOf` and `attribute`.", "cursor"));
        if (!Enum.IsDefined(request.Check))
            return Task.FromResult(MemoryRules.Fail<SpacedRecall>(MemoryStatus.Invalid, "check must be \"due\" or \"fresh\".", "check"));

        return SpacesAsync(context, request.Space,
            new MemoryQuery(Guid.Empty, request.Text, request.Subject, request.Attribute, request.AsOf, request.History, Limit: request.Limit,
                Check: request.Check), ct);
    }

    /// <summary>
    /// A timeline as the API and the memory_timeline tool both take it: facts in date order, ended and corrected ones
    /// included. One named space without a window or an attribute is read newest first a page at a time, each page in
    /// date order; with a window only the facts that started or ended in it are read, unpaged, and a cursor is refused.
    /// </summary>
    public async Task<Attempt<SpacedRecall, MemoryStatus>> TimelineAsync(
        OperationContext context, MemoryTimelineRequest request, CancellationToken ct = default)
    {
        bool windowed = request.From is not null || request.To is not null;
        Attempt<SpacedRecall, MemoryStatus> read;
        if (!string.IsNullOrWhiteSpace(request.Space) && !windowed && string.IsNullOrWhiteSpace(request.Attribute))
        {
            MemoryFactFilter filter = new(Text: request.Text, Subject: request.Subject, Status: MemoryFactStatus.All);
            read = await PageAsync(context, request.Space, new MemoryFactListing(filter, MemoryFactSort.ValidFrom, request.Cursor,
                Math.Clamp(request.Limit, 1, MemoryPaging.MaxLimit)), ct);
        }
        else if (!string.IsNullOrWhiteSpace(request.Cursor))
        {
            return MemoryRules.Fail<SpacedRecall>(MemoryStatus.Invalid,
                "cursor works only when you name a `space` and leave out `from`, `to` and `attribute`.", "cursor");
        }
        else
        {
            read = await SpacesAsync(context, request.Space, new MemoryQuery(Guid.Empty, request.Text, request.Subject, request.Attribute,
                History: !windowed, ChangedFrom: request.From, ChangedTo: request.To, Limit: request.Limit), ct);
        }

        if (!read.Succeeded)
            return read;
        SpacedRecall result = read.Result!;
        return Attempt<SpacedRecall, MemoryStatus>.Succeed(result with
        {
            Facts = [.. result.Facts.OrderBy(f => f.Fact.ValidFrom ?? DateTimeOffset.MinValue).ThenBy(f => f.Fact.RecordedAt)],
        });
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

        MemoryRecall recall = await memory.RecallAsync(query with { ContainerId = container }, caller, ct);
        return recall.Denied
            ? MemoryRules.Fail<IReadOnlyList<RecalledFact>>(MemoryStatus.Unchecked, "access to memory sources could not be checked; nothing is shown.")
            : Attempt<IReadOnlyList<RecalledFact>, MemoryStatus>.Succeed(recall.Facts);
    }
}

/// <summary>
/// What to recall: one <paramref name="Space"/> by id or name, or every space the caller can see when it is blank;
/// facts matching <paramref name="Text"/>, <paramref name="Subject"/> and <paramref name="Attribute"/>, true at
/// <paramref name="AsOf"/> (now when null), ended and corrected ones too with <paramref name="History"/>; at most
/// <paramref name="Limit"/>; and the <paramref name="Cursor"/> a previous page of one space returned.
/// <paramref name="Check"/> keeps only the facts past their check-by date (due), or only the rest (fresh).
/// </summary>
public sealed record MemoryRecallRequest(
    string? Space = null,
    string? Text = null,
    string? Subject = null,
    string? Attribute = null,
    DateTimeOffset? AsOf = null,
    bool History = false,
    int Limit = 10,
    string? Cursor = null,
    MemoryFactCheck Check = MemoryFactCheck.Any);

/// <summary>
/// What a timeline reads: one <paramref name="Space"/> by id or name, or every space the caller can see when it is
/// blank; facts matching <paramref name="Text"/>, <paramref name="Subject"/> and <paramref name="Attribute"/>; with
/// <paramref name="From"/>/<paramref name="To"/>, only those that started or ended in that window; at most
/// <paramref name="Limit"/>; and the <paramref name="Cursor"/> a previous page of one space returned.
/// </summary>
public sealed record MemoryTimelineRequest(
    string? Space = null,
    string? Text = null,
    string? Subject = null,
    string? Attribute = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 50,
    string? Cursor = null);

/// <summary>
/// Facts read from one or more spaces, with the spaces they came from for labelling, and how many spaces an unnamed
/// read left out for being past <see cref="RecallMemory.MaxSpaces"/>. <paramref name="NextCursor"/> continues a paged
/// read of one space, and is null on its last page and for every other read.
/// </summary>
public sealed record SpacedRecall(
    IReadOnlyList<RecalledFact> Facts, IReadOnlyDictionary<Guid, Container> Spaces, int SkippedSpaces, string? NextCursor = null)
{
    /// <summary>
    /// One list from several spaces' answers, at most <paramref name="limit"/> long. Ranked answers interleave by
    /// position, each space's best first, since ranks from separate reads aren't comparable; unranked ones go newest
    /// first, ties by id so the order is stable.
    /// </summary>
    public static IReadOnlyList<RecalledFact> Merge(IReadOnlyList<IReadOnlyList<RecalledFact>> perSpace, bool ranked, int limit)
    {
        int take = Math.Max(0, limit);
        if (!ranked)
        {
            return [.. perSpace.SelectMany(f => f)
                .OrderByDescending(f => f.Fact.RecordedAt)
                .ThenBy(f => f.Fact.Id)
                .Take(take)];
        }
        List<RecalledFact> merged = [];
        int deepest = perSpace.Count == 0 ? 0 : perSpace.Max(f => f.Count);
        for (int position = 0; position < deepest && merged.Count < take; position++)
        {
            foreach (IReadOnlyList<RecalledFact> facts in perSpace)
            {
                if (position < facts.Count && merged.Count < take)
                    merged.Add(facts[position]);
            }
        }
        return merged;
    }
}

/// <summary>Every memory operation in one place, for the container page's Memory tab.</summary>
public sealed class MemoryOperations(
    RememberFact remember, CorrectFact correct, ForgetFact forget, RecallMemory recall, ListMemoryHistory history,
    ListMemoryFacts list, CountMemoryFacts count, ListMemorySubjects subjects, ConfirmFact confirm)
{
    public ConfirmFact Confirm { get; } = confirm;

    public ListMemoryFacts List { get; } = list;

    public CountMemoryFacts Count { get; } = count;

    public ListMemorySubjects Subjects { get; } = subjects;

    public ListMemoryHistory History { get; } = history;

    public RememberFact Remember { get; } = remember;

    public CorrectFact Correct { get; } = correct;

    public ForgetFact Forget { get; } = forget;

    public RecallMemory Recall { get; } = recall;
}

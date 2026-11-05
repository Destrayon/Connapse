using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Application.Containers;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Application.Memory;

/// <summary>Why a memory space action didn't succeed.</summary>
public enum MemorySpaceStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "memory-space-not-found", "Memory space not found")]
    NotFound,

    [Failure(400, "invalid-memory-space", "Invalid memory space")]
    Invalid,

    [Failure(409, "memory-space-conflict", "Memory space already exists")]
    Conflict,

    [Failure(409, "memory-space-is-container", "Documents container")]
    DocumentContainer,

    [Failure(400, "invalid-page", "Invalid page")]
    InvalidPage,

    [Failure(503, "memory-access-unchecked", "Access could not be checked")]
    Unchecked,
}

/// <summary>
/// What kind of place memory lives in: a personal space only its owner sees, a team space everyone sees, or a
/// documents container that holds facts alongside its files.
/// </summary>
public enum MemorySpaceKind
{
    Personal,
    Team,
    Container,
}

/// <summary>How the list is ordered after starred spaces: newest change first, by name, or most facts first.</summary>
public enum MemorySpaceSort
{
    Updated,
    Name,
    Facts,
}

/// <summary>
/// One page of the spaces list. <see cref="Kind"/>, <see cref="Q"/> and <see cref="Starred"/> narrow it; a
/// <see cref="Cursor"/> must come from a page asked with the same filters and sort.
/// </summary>
public sealed record MemorySpaceQuery(
    MemorySpaceKind? Kind = null,
    string? Q = null,
    bool Starred = false,
    MemorySpaceSort Sort = MemorySpaceSort.Updated,
    int? Limit = null,
    string? Cursor = null);

/// <summary>A space as the Memory page lists it: its current facts the caller may see, and the newest change.</summary>
public sealed record MemorySpaceView(
    string Id,
    string Name,
    string? Description,
    MemorySpaceKind Kind,
    int Facts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Starred);

/// <summary>How many spaces each tab of the list holds, with the text filter applied and no other.</summary>
public sealed record MemorySpaceCounts(int All, int Starred, int Personal, int Team, int Containers);

/// <summary>A page of spaces, the cursor to the next one (null on the last), and how many match the filters.</summary>
public sealed record MemorySpacePage(IReadOnlyList<MemorySpaceView> Items, string? NextCursor, int Total, MemorySpaceCounts Counts);

/// <summary>Whether the caller may add to the spaces listed, and how many accounts everyone-visible memory reaches.</summary>
public sealed record MemorySpaceAudience(bool CanAdd, int? Accounts);

/// <summary>A new space: a name, an optional description, and whether it is personal or team.</summary>
public sealed record NewMemorySpace(string? Name, string? Description, MemorySpaceKind? Kind);

/// <summary>What an edit changes; a null field is left as it is.</summary>
public sealed record MemorySpaceChanges(string? Name = null, string? Description = null);

/// <summary>
/// What a delete removes, or would remove on a dry run: the facts (each counted once however often corrected), every
/// stored version of them, and how many people or agents wrote them.
/// </summary>
public sealed record MemorySpaceDeletion(string Id, string Name, MemorySpaceKind Kind, int Facts, int Versions, int Authors, bool Deleted);

internal static class MemorySpaceRules
{
    public static Attempt<T, MemorySpaceStatus> Fail<T>(MemorySpaceStatus status, string detail, string? field = null) =>
        Attempt<T, MemorySpaceStatus>.Fail(status, detail, field);

    public static Attempt<T, MemorySpaceStatus> NotFound<T>(Guid id) =>
        Fail<T>(MemorySpaceStatus.NotFound, $"Memory space {id} not found");

    public static MemorySpaceKind KindOf(Container container) =>
        container.Kind == ContainerKind.Documents ? MemorySpaceKind.Container
        : container.Audience == MemoryScope.Personal ? MemorySpaceKind.Personal
        : MemorySpaceKind.Team;

    public static MemorySpaceView View(Container container, int facts, DateTimeOffset updatedAt, bool starred) =>
        new(container.Id, container.Name, container.Description, KindOf(container), facts,
            new DateTimeOffset(DateTime.SpecifyKind(container.CreatedAt, DateTimeKind.Utc)), updatedAt, starred);

    public static MemorySpaceView View(Container container) =>
        View(container, 0, new DateTimeOffset(DateTime.SpecifyKind(container.UpdatedAt, DateTimeKind.Utc)), false);

    /// <summary>
    /// The space <paramref name="id"/> names as the caller may see it, when it is a memory space: a documents container
    /// is managed through the container routes, and someone else's personal space reads as missing.
    /// </summary>
    public static async Task<(Container? Space, Attempt<T, MemorySpaceStatus>? Refused)> MemorySpaceAsync<T>(
        IContainerStore containers, Guid id, MemoryCaller viewer, CancellationToken ct)
    {
        Container? found = await containers.GetAsync(id, viewer, ct);
        if (found is null)
            return (null, NotFound<T>(id));
        if (found.Kind != ContainerKind.Memory)
            return (null, Fail<T>(MemorySpaceStatus.DocumentContainer,
                $"'{found.Name}' is a documents container, not a memory space. Rename or delete it with /api/containers/{found.Id}."));
        return (found, null);
    }
}

/// <summary>
/// Every place the caller may see memory in: their personal spaces, every team space, and each documents container
/// holding a current fact they may see, with per-space counts. Starred spaces come first.
/// </summary>
[Operation("memorySpaces.list", Scope = MemoryRules.ReadScope, Risk = RiskTier.Read)]
public sealed class ListMemorySpaces(IMemorySpaceStore spaces, IOperationAuthorizer authorizer, IUserDirectory? people = null)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    /// <summary>
    /// Who a listed space's facts reach, for telling an agent in words: whether the caller may add to the spaces it
    /// sees (every save needs write access, and the personal spaces listed are the caller's own), and how many accounts
    /// a team space or documents container is shared with (null when that can't be counted).
    /// </summary>
    public async Task<MemorySpaceAudience> AudienceAsync(OperationContext context, CancellationToken ct = default)
    {
        bool canAdd = await MemoryRules.WriterAsync(authorizer, context);
        int? accounts = people is null ? null : (await people.ListAsync(0, 0, ct)).TotalCount;
        return new MemorySpaceAudience(canAdd, accounts);
    }

    public async Task<Attempt<MemorySpacePage, MemorySpaceStatus>> RunAsync(OperationContext context, MemorySpaceQuery query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return MemorySpaceRules.Fail<MemorySpacePage>(MemorySpaceStatus.Forbidden, "Listing memory spaces needs knowledge:read.");
        if (query.Kind is { } kind && !Enum.IsDefined(kind))
            return MemorySpaceRules.Fail<MemorySpacePage>(MemorySpaceStatus.Invalid, "kind must be personal, team or container", "kind");
        if (!Enum.IsDefined(query.Sort))
            return MemorySpaceRules.Fail<MemorySpacePage>(MemorySpaceStatus.Invalid, "sort must be updated, name or facts", "sort");
        if (query.Limit is < 1 or > MaxLimit)
            return MemorySpaceRules.Fail<MemorySpacePage>(MemorySpaceStatus.InvalidPage, $"limit must be between 1 and {MaxLimit}", "limit");

        string? text = string.IsNullOrWhiteSpace(query.Q) ? null : query.Q.Trim();
        string filters = SpaceCursor.Filters(query.Kind, text, query.Starred, query.Sort);
        SpaceCursor? after = null;
        if (query.Cursor is not null)
        {
            after = SpaceCursor.Parse(query.Cursor);
            if (after is null || after.F != filters)
                return MemorySpaceRules.Fail<MemorySpacePage>(MemorySpaceStatus.InvalidPage,
                    "cursor isn't one this list returned for the same kind, q, starred and sort", "cursor");
        }

        MemoryCaller viewer = MemoryRules.Caller(context.User);
        IReadOnlyList<MemorySpaceCandidate> candidates = await spaces.CandidatesAsync(viewer, text, ct);

        // A documents container is listed only when it holds a fact the caller may read. One with a fact citing no
        // source plainly does; the rest are settled by checking one fact each, and only a denied one costs more.
        List<string> unknown = [.. candidates
            .Where(c => c.Container.Kind == ContainerKind.Documents && c.FactsAtLeast == 0)
            .Select(c => c.Container.Id)];
        IReadOnlySet<string> holding = new HashSet<string>(StringComparer.Ordinal);
        if (unknown.Count > 0)
        {
            if (await spaces.HoldingReadableAsync(viewer, unknown, ct) is not { } held)
                return Unchecked();
            holding = held;
        }
        List<MemorySpaceCandidate> listed = [.. candidates.Where(c =>
            c.Container.Kind != ContainerKind.Documents || c.FactsAtLeast > 0 || holding.Contains(c.Container.Id))];

        MemorySpaceCounts counts = new(
            listed.Count,
            listed.Count(c => c.Starred),
            listed.Count(c => MemorySpaceRules.KindOf(c.Container) == MemorySpaceKind.Personal),
            listed.Count(c => MemorySpaceRules.KindOf(c.Container) == MemorySpaceKind.Team),
            listed.Count(c => MemorySpaceRules.KindOf(c.Container) == MemorySpaceKind.Container));

        List<MemorySpaceCandidate> matching = [.. listed
            .Where(c => query.Kind is not { } k || MemorySpaceRules.KindOf(c.Container) == k)
            .Where(c => !query.Starred || c.Starred)];
        int limit = query.Limit ?? DefaultLimit;
        (List<SpaceCursor>? page, bool more) = await PageAsync(viewer, matching, query.Sort, filters, after, limit, ct);
        if (page is null)
            return Unchecked();
        return Attempt<MemorySpacePage, MemorySpaceStatus>.Succeed(
            new MemorySpacePage([.. page.Select(c => c.View!)], more ? page[^1].Encode() : null, matching.Count, counts));
    }

    private static Attempt<MemorySpacePage, MemorySpaceStatus> Unchecked() =>
        MemorySpaceRules.Fail<MemorySpacePage>(MemorySpaceStatus.Unchecked,
            "access to memory sources could not be checked, so no fact counts are shown.");

    /// <summary>
    /// The page after <paramref name="after"/>, with exact values checked only for spaces that could land on it. Each
    /// space's place lies between where its upper bounds and its lower bounds would put it. Spaces are checked a page at
    /// a time, in order of the earliest place each could take, until the page's last space comes before the earliest
    /// place any unchecked one could take. Null when access could not be checked.
    /// </summary>
    private async Task<(List<SpaceCursor>? Page, bool More)> PageAsync(
        MemoryCaller viewer, List<MemorySpaceCandidate> matching, MemorySpaceSort sort, string filters, SpaceCursor? after,
        int limit, CancellationToken ct)
    {
        SpaceCursor Key(MemorySpaceCandidate c, int facts, DateTimeOffset updatedAt) =>
            SpaceCursor.Of(MemorySpaceRules.View(c.Container, facts, updatedAt, c.Starred), sort, filters);
        bool AfterCursor(SpaceCursor key) => after is null || SpaceCursor.Ordering.Compare(key, after) > 0;

        // A space whose latest possible place is at or before the cursor was on an earlier page, whatever its values.
        List<(MemorySpaceCandidate Candidate, SpaceCursor Earliest)> waiting = [.. matching
            .Where(c => AfterCursor(Key(c, c.FactsAtLeast, c.UpdatedAtLeast)))
            .Select(c => (c, Key(c, c.FactsAtMost, c.UpdatedAtMost)))
            .OrderBy(w => w.Item2, SpaceCursor.Ordering)];

        List<SpaceCursor> placed = [];
        int next = 0;
        while (next < waiting.Count)
        {
            if (placed.Count >= limit && SpaceCursor.Ordering.Compare(placed[limit - 1], waiting[next].Earliest) < 0)
                break;
            List<MemorySpaceCandidate> batch = [.. waiting.Skip(next).Take(limit).Select(w => w.Candidate)];
            next += batch.Count;
            List<string> unsettled = [.. batch.Where(c => !c.Settled).Select(c => c.Container.Id)];
            IReadOnlyDictionary<string, MemorySpaceTally> tallies = new Dictionary<string, MemorySpaceTally>(StringComparer.Ordinal);
            if (unsettled.Count > 0)
            {
                if (await spaces.TallyAsync(viewer, unsettled, ct) is not { } tallied)
                    return (null, false);
                tallies = tallied;
            }
            foreach (MemorySpaceCandidate c in batch)
            {
                SpaceCursor? key = c.Settled ? Key(c, c.FactsAtLeast, c.UpdatedAtLeast)
                    : tallies.TryGetValue(c.Container.Id, out MemorySpaceTally? tally)
                        && (tally.Facts > 0 || c.Container.Kind != ContainerKind.Documents) ? Key(c, tally.Facts, tally.UpdatedAt)
                    : null; // Gone, no longer visible, or a documents container with nothing left the reader may see.
                if (key is not null && AfterCursor(key))
                    placed.Add(key);
            }
            placed.Sort(SpaceCursor.Ordering);
        }

        // Every space still waiting comes after the page's last one, so it is on a later page.
        return ([.. placed.Take(limit)], placed.Count > limit || next < waiting.Count);
    }
}

/// <summary>
/// Where a page ends: the last item's sort key, and the filters it was asked with. Opaque to callers, base64url JSON.
/// </summary>
internal sealed record SpaceCursor(bool S, long N, string T, string I, string F)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public MemorySpaceView? View { get; init; }

    /// <summary>Starred first, then the sort's order (numbers largest first, text A to Z), then the id.</summary>
    public static readonly IComparer<SpaceCursor> Ordering = Comparer<SpaceCursor>.Create((a, b) =>
    {
        int c = b.S.CompareTo(a.S);
        if (c == 0)
            c = b.N.CompareTo(a.N);
        if (c == 0)
            c = string.CompareOrdinal(a.T, b.T);
        return c != 0 ? c : string.CompareOrdinal(a.I, b.I);
    });

    public static string Filters(MemorySpaceKind? kind, string? text, bool starred, MemorySpaceSort sort)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{text}|{starred}|{sort}"));
        return Convert.ToHexString(hash, 0, 8);
    }

    public static SpaceCursor Of(MemorySpaceView view, MemorySpaceSort sort, string filters) => sort switch
    {
        MemorySpaceSort.Name => new(view.Starred, 0, view.Name, view.Id, filters) { View = view },
        MemorySpaceSort.Facts => new(view.Starred, view.Facts, "", view.Id, filters) { View = view },
        _ => new(view.Starred, view.UpdatedAt.UtcTicks, "", view.Id, filters) { View = view },
    };

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this));

    public static SpaceCursor? Parse(string cursor)
    {
        try
        {
            SpaceCursor? parsed = JsonSerializer.Deserialize<SpaceCursor>(Base64Url.DecodeFromChars(cursor));
            return parsed is { T: not null, I: not null, F: not null } ? parsed : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Makes a memory space. A team space needs write access and a name no team container uses; a personal space belongs
/// to the caller and needs a name they don't already use.
/// </summary>
[Operation("memorySpaces.create", Scope = MemoryRules.WriteScope, Risk = RiskTier.Write)]
public sealed class CreateMemorySpace(IContainerStore containers, IMemorySpaceStore spaces, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<MemorySpaceView, MemorySpaceStatus>> RunAsync(OperationContext context, NewMemorySpace request, CancellationToken ct = default)
    {
        static Attempt<MemorySpaceView, MemorySpaceStatus> Fail(MemorySpaceStatus status, string detail, string? field = null) =>
            MemorySpaceRules.Fail<MemorySpaceView>(status, detail, field);

        if (!await MemoryRules.WriterAsync(authorizer, context))
            return Fail(MemorySpaceStatus.Forbidden, "Making a memory space needs write access (knowledge:write).");
        string name = request.Name?.Trim() ?? "";
        if (name.Length == 0)
            return Fail(MemorySpaceStatus.Invalid, "A space name is required", "name");
        if (!PathUtilities.IsValidContainerName(name))
            return Fail(MemorySpaceStatus.Invalid, ContainerRules.NameRule, "name");
        string? description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        Container created;
        switch (request.Kind)
        {
            case MemorySpaceKind.Team:
                if (await containers.GetByNameAsync(name, ct) is not null)
                    return Fail(MemorySpaceStatus.Conflict, $"A team space or container named '{name}' already exists", "name");
                try
                {
                    created = await containers.CreateAsync(new CreateContainerRequest(name, description, ContainerKind.Memory), ct);
                }
                catch (InvalidOperationException ex)
                {
                    return Fail(MemorySpaceStatus.Conflict, ex.Message, "name");
                }
                break;
            case MemorySpaceKind.Personal:
                MemoryCaller owner = MemoryRules.Caller(context.User);
                if (!MemoryRules.CanOwn(owner))
                    return Fail(MemorySpaceStatus.Forbidden, "A personal space needs a signed-in user or agent to belong to.");
                if (await spaces.CreatePersonalAsync(owner, name, description, ct) is not { } personal)
                    return Fail(MemorySpaceStatus.Conflict, $"You already have a personal space named '{name}'", "name");
                created = personal;
                break;
            case MemorySpaceKind.Container:
                return Fail(MemorySpaceStatus.Invalid, "A documents container is made with POST /api/containers; kind must be personal or team", "kind");
            default:
                return Fail(MemorySpaceStatus.Invalid, "kind is required: personal or team", "kind");
        }

        await audit.LogAsync("memory_space.created", "container", created.Id,
            new { created.Name, Kind = MemorySpaceRules.KindOf(created).ToString().ToLowerInvariant() }, ct);
        return Attempt<MemorySpaceView, MemorySpaceStatus>.Succeed(MemorySpaceRules.View(created));
    }
}

/// <summary>Renames a memory space or changes its description. Write access; a personal space only by its owner.</summary>
[Operation("memorySpaces.update", Scope = MemoryRules.WriteScope, Risk = RiskTier.Write)]
public sealed class UpdateMemorySpace(IContainerStore containers, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<MemorySpaceView, MemorySpaceStatus>> RunAsync(
        OperationContext context, Guid spaceId, MemorySpaceChanges changes, CancellationToken ct = default)
    {
        static Attempt<MemorySpaceView, MemorySpaceStatus> Fail(MemorySpaceStatus status, string detail, string? field = null) =>
            MemorySpaceRules.Fail<MemorySpaceView>(status, detail, field);

        if (!await MemoryRules.WriterAsync(authorizer, context))
            return Fail(MemorySpaceStatus.Forbidden, "Changing a memory space needs write access (knowledge:write).");
        if (changes.Name is null && changes.Description is null)
            return Fail(MemorySpaceStatus.Invalid, "A new name or description is required");
        string? name = changes.Name?.Trim();
        if (name is not null && !PathUtilities.IsValidContainerName(name))
            return Fail(MemorySpaceStatus.Invalid, name.Length == 0 ? "A space name is required" : ContainerRules.NameRule, "name");

        MemoryCaller viewer = MemoryRules.Caller(context.User);
        (Container? current, Attempt<MemorySpaceView, MemorySpaceStatus>? refused) =
            await MemorySpaceRules.MemorySpaceAsync<MemorySpaceView>(containers, spaceId, viewer, ct);
        if (refused is not null)
            return refused;
        // A name is taken only among spaces of the same audience: team ones, or the owner's own personal spaces.
        if (name is not null && name != current!.Name
            && (await containers.FindByNameAsync(name, viewer, ct)).Any(c => c.Audience == current.Audience))
            return Fail(MemorySpaceStatus.Conflict, $"A space named '{name}' already exists", "name");

        Container? updated;
        try
        {
            updated = await containers.UpdateAsync(spaceId, name, changes.Description?.Trim(), ct);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(MemorySpaceStatus.Conflict, ex.Message, "name");
        }
        if (updated is null)
            return MemorySpaceRules.NotFound<MemorySpaceView>(spaceId);

        await audit.LogAsync("memory_space.updated", "container", updated.Id, new { From = current!.Name, updated.Name }, ct);
        return Attempt<MemorySpaceView, MemorySpaceStatus>.Succeed(MemorySpaceRules.View(updated));
    }
}

/// <summary>
/// Deletes a memory space and every fact in it, whoever wrote them. Write access; a personal space only by its owner.
/// The report counts only the facts the caller may read; a dry run reports without deleting.
/// </summary>
[Operation("memorySpaces.delete", Scope = MemoryRules.WriteScope, Risk = RiskTier.Destructive)]
public sealed class DeleteMemorySpace(IContainerStore containers, IMemorySpaceStore spaces, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<MemorySpaceDeletion, MemorySpaceStatus>> RunAsync(
        OperationContext context, Guid spaceId, bool dryRun, CancellationToken ct = default)
    {
        // The report counts facts, so it needs read access as well as write.
        if (!await MemoryRules.WriterAsync(authorizer, context) || !await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return MemorySpaceRules.Fail<MemorySpaceDeletion>(MemorySpaceStatus.Forbidden,
                "Deleting a memory space needs write and read access (knowledge:write and knowledge:read).");

        MemoryCaller viewer = MemoryRules.Caller(context.User);
        (Container? space, Attempt<MemorySpaceDeletion, MemorySpaceStatus>? refused) =
            await MemorySpaceRules.MemorySpaceAsync<MemorySpaceDeletion>(containers, spaceId, viewer, ct);
        if (refused is not null)
            return refused;

        if (await spaces.ImpactAsync(spaceId, viewer, ct) is not { } impact)
            return MemorySpaceRules.Fail<MemorySpaceDeletion>(MemorySpaceStatus.Unchecked,
                "access to memory sources could not be checked, so nothing was deleted.");
        MemorySpaceDeletion deletion = new(space!.Id, space.Name, MemorySpaceRules.KindOf(space), impact.Facts, impact.Versions, impact.Authors, false);
        if (dryRun)
            return Attempt<MemorySpaceDeletion, MemorySpaceStatus>.Succeed(deletion);

        if (!await containers.DeleteAsync(spaceId, ct))
            return MemorySpaceRules.NotFound<MemorySpaceDeletion>(spaceId);
        await audit.LogAsync("memory_space.deleted", "container", space.Id,
            new { space.Name, deletion.Kind, impact.Facts, impact.Versions, impact.Authors }, ct);
        return Attempt<MemorySpaceDeletion, MemorySpaceStatus>.Succeed(deletion with { Deleted = true });
    }
}

/// <summary>Stars a space or documents container for the caller alone, so it sorts first in their list.</summary>
[Operation("memorySpaces.star", Scope = MemoryRules.ReadScope, Risk = RiskTier.Write)]
public sealed class StarMemorySpace(IContainerStore containers, IMemorySpaceStore spaces, IOperationAuthorizer authorizer)
{
    public Task<Attempt<bool, MemorySpaceStatus>> RunAsync(OperationContext context, Guid spaceId, CancellationToken ct = default) =>
        MemorySpaceStars.SetAsync(containers, authorizer, context, spaceId, caller => spaces.StarAsync(spaceId, caller, ct), ct);
}

/// <summary>Removes the caller's star from a space. Unstarring one that isn't starred does nothing.</summary>
[Operation("memorySpaces.unstar", Scope = MemoryRules.ReadScope, Risk = RiskTier.Write)]
public sealed class UnstarMemorySpace(IContainerStore containers, IMemorySpaceStore spaces, IOperationAuthorizer authorizer)
{
    public Task<Attempt<bool, MemorySpaceStatus>> RunAsync(OperationContext context, Guid spaceId, CancellationToken ct = default) =>
        MemorySpaceStars.SetAsync(containers, authorizer, context, spaceId, caller => spaces.UnstarAsync(spaceId, caller, ct), ct);
}

internal static class MemorySpaceStars
{
    /// <summary>
    /// A star is the caller's own preference, so reading is enough; the space must be one they may see, and they must
    /// be someone a star can belong to.
    /// </summary>
    public static async Task<Attempt<bool, MemorySpaceStatus>> SetAsync(
        IContainerStore containers, IOperationAuthorizer authorizer, OperationContext context, Guid spaceId,
        Func<MemoryCaller, Task> apply, CancellationToken ct)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return MemorySpaceRules.Fail<bool>(MemorySpaceStatus.Forbidden, "Starring a memory space needs knowledge:read.");
        MemoryCaller caller = MemoryRules.Caller(context.User);
        if (!MemoryRules.CanOwn(caller))
            return MemorySpaceRules.Fail<bool>(MemorySpaceStatus.Forbidden, "A star needs a signed-in user or agent to belong to.");
        if (await containers.GetAsync(spaceId, caller, ct) is null)
            return MemorySpaceRules.NotFound<bool>(spaceId);
        await apply(caller);
        return Attempt<bool, MemorySpaceStatus>.Succeed(true);
    }
}

/// <summary>Every memory space operation in one place, for the Memory page.</summary>
public sealed class MemorySpaceOperations(
    ListMemorySpaces list, CreateMemorySpace create, UpdateMemorySpace update, DeleteMemorySpace delete,
    StarMemorySpace star, UnstarMemorySpace unstar)
{
    public ListMemorySpaces List { get; } = list;

    public CreateMemorySpace Create { get; } = create;

    public UpdateMemorySpace Update { get; } = update;

    public DeleteMemorySpace Delete { get; } = delete;

    public StarMemorySpace Star { get; } = star;

    public UnstarMemorySpace Unstar { get; } = unstar;
}

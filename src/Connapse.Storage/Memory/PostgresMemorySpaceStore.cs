using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Connapse.Storage.Memory;

/// <summary>
/// Spaces as the Memory page lists them. Fact counts follow the same rule as reading a fact: a current fact counts for
/// a viewer only when it is team or theirs and they can read every source it cites. Candidates come with bounds and no
/// source checked; only the spaces a page shows are tallied exactly, and a documents container's presence is settled by
/// checking one fact first. A delete's impact counts by the same rule, so it never reveals facts the viewer can't read.
/// </summary>
public sealed class PostgresMemorySpaceStore(
    IDbContextFactory<KnowledgeDbContext> factory,
    IDocumentAccess access,
    ILinkIndex links,
    TimeProvider clock,
    ILogger<PostgresMemorySpaceStore> logger) : IMemorySpaceStore
{
    // The viewer may see the container: every team one, and their own personal spaces (an agent acting for no user
    // owns its own). Same rule as PostgresContainerStore.VisibleTo.
    private const string ContainerVisible =
        "(c.audience = 'team' OR (@user IS NOT NULL AND c.owner_user_id = @user) "
        + "OR (@user IS NULL AND @agent IS NOT NULL AND c.owner_user_id IS NULL AND c.owner_agent_id = @agent))";

    // The fact is team, or the viewer wrote it: MemoryCaller.IsAuthorOf in SQL.
    private const string FactScopeVisible =
        "(f.scope = 1 OR (@user IS NOT NULL AND f.author_user_id = @user) "
        + "OR (@user IS NULL AND @agent IS NOT NULL AND f.author_user_id IS NULL AND f.author_agent_id = @agent))";

    // Current as of now: not corrected, already true, and not yet ended. The same test as a recall's current facts.
    private const string FactCurrent =
        "f.superseded_at IS NULL AND (f.valid_from IS NULL OR f.valid_from <= @now) AND (f.valid_to IS NULL OR f.valid_to > @now)";

    public async Task<IReadOnlyList<MemorySpaceSummary>?> ListAsync(MemoryCaller viewer, string? text, bool withEnded = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        DateTimeOffset now = clock.GetUtcNow();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);

        string? pattern = string.IsNullOrWhiteSpace(text) ? null : "%" + EscapeLike(text.Trim()) + "%";
        // Only the spaces the text matches have their sources checked, so a narrow search stays cheap.
        IReadOnlySet<MemorySource>? readable = await ReadableAsync(
            context, viewer, withEnded ? CitedEverInVisibleSpaces : CitedInVisibleSpaces,
            [.. Viewer(viewer, now), new NpgsqlParameter("pattern", NpgsqlDbType.Text) { Value = (object?)pattern ?? DBNull.Value }],
            "listing no spaces", ct);
        if (readable is null)
            return null;

        (Guid? starUser, Guid? starAgent) = OwnerOf(viewer);
        List<NpgsqlParameter> parameters =
        [
            .. Viewer(viewer, now),
            .. Readable(readable),
            new("pattern", NpgsqlDbType.Text) { Value = (object?)pattern ?? DBNull.Value },
            new("star_user", NpgsqlDbType.Uuid) { Value = (object?)starUser ?? DBNull.Value },
            new("star_agent", NpgsqlDbType.Uuid) { Value = (object?)starAgent ?? DBNull.Value },
            new("with_ended", NpgsqlDbType.Boolean) { Value = withEnded },
        ];

        // One grouped pass over the visible current facts gives every row its count and newest fact.
        List<SpaceRow> rows = await context.Database.SqlQueryRaw<SpaceRow>(
            $"""
            SELECT c.id AS "Id", c.name AS "Name", c.description AS "Description", c.kind AS "Kind",
                   c.audience AS "Audience", c.owner_user_id AS "OwnerUserId", c.owner_agent_id AS "OwnerAgentId",
                   c.created_at AS "CreatedAt", c.updated_at AS "UpdatedAt",
                   COALESCE(a.facts, 0) AS "Facts", a.newest AS "Newest",
                   EXISTS (SELECT 1 FROM memory_space_stars st WHERE st.container_id = c.id
                           AND (@star_user IS NOT NULL OR @star_agent IS NOT NULL)
                           AND st.user_id IS NOT DISTINCT FROM @star_user
                           AND st.agent_id IS NOT DISTINCT FROM @star_agent) AS "Starred"
            FROM containers c
            LEFT JOIN (
                SELECT f.container_id, count(*) AS facts, max(f.recorded_at) AS newest
                FROM memory_facts f
                WHERE {FactCurrent} AND {FactScopeVisible} AND {SourcesReadable}
                GROUP BY f.container_id
            ) a ON a.container_id = c.id
            WHERE {ContainerVisible}
              AND (c.kind = 'memory' OR COALESCE(a.facts, 0) > 0
                   OR (@with_ended AND EXISTS (SELECT 1 FROM memory_facts f
                       WHERE f.container_id = c.id AND {FactScopeVisible} AND {SourcesReadable})))
              AND {SpaceMatches}
            """,
            [.. parameters]).ToListAsync(ct);

        return rows.Select(r => new MemorySpaceSummary(
                new Container(
                    r.Id.ToString(), r.Name, r.Description, r.CreatedAt.UtcDateTime, r.UpdatedAt.UtcDateTime,
                    Kind: r.Kind == "memory" ? ContainerKind.Memory : ContainerKind.Documents,
                    Audience: r.Audience == "personal" ? MemoryScope.Personal : MemoryScope.Team)
                {
                    OwnerUserId = r.OwnerUserId,
                    OwnerAgentId = r.OwnerAgentId,
                },
                (int)r.Facts,
                Later(r.UpdatedAt, r.Newest),
                r.Starred))
            .ToList();
    }

    public async Task<IReadOnlyList<MemorySpaceCandidate>> CandidatesAsync(MemoryCaller viewer, string? text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        DateTimeOffset now = clock.GetUtcNow();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);

        string? pattern = string.IsNullOrWhiteSpace(text) ? null : "%" + EscapeLike(text.Trim()) + "%";
        (Guid? starUser, Guid? starAgent) = OwnerOf(viewer);
        List<NpgsqlParameter> parameters =
        [
            .. Viewer(viewer, now),
            new("pattern", NpgsqlDbType.Text) { Value = (object?)pattern ?? DBNull.Value },
            new("star_user", NpgsqlDbType.Uuid) { Value = (object?)starUser ?? DBNull.Value },
            new("star_agent", NpgsqlDbType.Uuid) { Value = (object?)starAgent ?? DBNull.Value },
        ];

        // One grouped pass over the current facts in the viewer's scope gives every row its bounds. A fact citing no
        // source is readable by anyone it is in scope for, so those facts alone give the lower bounds.
        List<CandidateRow> rows = await context.Database.SqlQueryRaw<CandidateRow>(
            $"""
            SELECT c.id AS "Id", c.name AS "Name", c.description AS "Description", c.kind AS "Kind",
                   c.audience AS "Audience", c.owner_user_id AS "OwnerUserId", c.owner_agent_id AS "OwnerAgentId",
                   c.created_at AS "CreatedAt", c.updated_at AS "UpdatedAt",
                   COALESCE(a.sourceless, 0) AS "FactsAtLeast", COALESCE(a.facts, 0) AS "FactsAtMost",
                   a.newest_sourceless AS "NewestAtLeast", a.newest AS "NewestAtMost",
                   EXISTS (SELECT 1 FROM memory_space_stars st WHERE st.container_id = c.id
                           AND (@star_user IS NOT NULL OR @star_agent IS NOT NULL)
                           AND st.user_id IS NOT DISTINCT FROM @star_user
                           AND st.agent_id IS NOT DISTINCT FROM @star_agent) AS "Starred"
            FROM containers c
            LEFT JOIN (
                SELECT f.container_id, count(*) AS facts, max(f.recorded_at) AS newest,
                       count(*) FILTER (WHERE cited.fact_id IS NULL) AS sourceless,
                       max(f.recorded_at) FILTER (WHERE cited.fact_id IS NULL) AS newest_sourceless
                FROM memory_facts f
                LEFT JOIN (SELECT DISTINCT s.fact_id FROM memory_fact_sources s) cited ON cited.fact_id = f.id
                WHERE {FactCurrent} AND {FactScopeVisible}
                GROUP BY f.container_id
            ) a ON a.container_id = c.id
            WHERE {ContainerVisible}
              AND (c.kind = 'memory' OR COALESCE(a.facts, 0) > 0)
              AND {SpaceMatches}
            """,
            [.. parameters]).ToListAsync(ct);

        return rows.Select(r => new MemorySpaceCandidate(
                new Container(
                    r.Id.ToString(), r.Name, r.Description, r.CreatedAt.UtcDateTime, r.UpdatedAt.UtcDateTime,
                    Kind: r.Kind == "memory" ? ContainerKind.Memory : ContainerKind.Documents,
                    Audience: r.Audience == "personal" ? MemoryScope.Personal : MemoryScope.Team)
                {
                    OwnerUserId = r.OwnerUserId,
                    OwnerAgentId = r.OwnerAgentId,
                },
                r.Starred,
                (int)r.FactsAtLeast,
                (int)r.FactsAtMost,
                Later(r.UpdatedAt, r.NewestAtLeast),
                Later(r.UpdatedAt, r.NewestAtMost)))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, MemorySpaceTally>?> TallyAsync(
        MemoryCaller viewer, IReadOnlyCollection<string> containerIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        Guid[] ids = Ids(containerIds);
        if (ids.Length == 0)
            return new Dictionary<string, MemorySpaceTally>(StringComparer.Ordinal);
        DateTimeOffset now = clock.GetUtcNow();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);

        // Only the named spaces' sources are checked.
        IReadOnlySet<MemorySource>? readable = await ReadableAsync(
            context, viewer, CitedInSpaces, [.. Viewer(viewer, now), Containers(ids)], "tallying no spaces", ct);
        if (readable is null)
            return null;

        List<TallyRow> rows = await context.Database.SqlQueryRaw<TallyRow>(
            $"""
            SELECT c.id AS "Id", c.updated_at AS "UpdatedAt", COALESCE(a.facts, 0) AS "Facts", a.newest AS "Newest"
            FROM containers c
            LEFT JOIN (
                SELECT f.container_id, count(*) AS facts, max(f.recorded_at) AS newest
                FROM memory_facts f
                WHERE f.container_id = ANY(@containers) AND {FactCurrent} AND {FactScopeVisible} AND {SourcesReadable}
                GROUP BY f.container_id
            ) a ON a.container_id = c.id
            WHERE c.id = ANY(@containers) AND {ContainerVisible}
            """,
            [.. Viewer(viewer, now), Containers(ids), .. Readable(readable)]).ToListAsync(ct);

        return rows.ToDictionary(
            r => r.Id.ToString(), r => new MemorySpaceTally((int)r.Facts, Later(r.UpdatedAt, r.Newest)), StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<string>?> HoldingReadableAsync(
        MemoryCaller viewer, IReadOnlyCollection<string> containerIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        Guid[] ids = Ids(containerIds);
        HashSet<string> holding = new(StringComparer.Ordinal);
        if (ids.Length == 0)
            return holding;
        DateTimeOffset now = clock.GetUtcNow();
        List<WitnessRow> witnesses;
        await using (KnowledgeDbContext context = await factory.CreateDbContextAsync(ct))
        {
            // One witness per space: a current fact in the viewer's scope citing the fewest sources, newest first. Its
            // sources are the only ones checked unless it turns out hidden.
            witnesses = await context.Database.SqlQueryRaw<WitnessRow>(
                $"""
                WITH witness AS (
                    SELECT DISTINCT ON (f.container_id) f.container_id, f.id
                    FROM memory_facts f
                    JOIN containers c ON c.id = f.container_id
                    WHERE f.container_id = ANY(@containers) AND {FactCurrent} AND {FactScopeVisible} AND {ContainerVisible}
                    ORDER BY f.container_id, (SELECT count(*) FROM memory_fact_sources s WHERE s.fact_id = f.id),
                             f.recorded_at DESC, f.id
                )
                SELECT w.container_id AS "Container", s.kind AS "Kind", s.ref AS "Ref"
                FROM witness w
                LEFT JOIN memory_fact_sources s ON s.fact_id = w.id
                """,
                [.. Viewer(viewer, now), Containers(ids)]).ToListAsync(ct);
        }

        static MemorySource? SourceOf(WitnessRow row) =>
            row.Kind is { } kind && row.Ref is { } reference ? new MemorySource((MemorySourceKind)kind, reference) : null;

        IReadOnlySet<MemorySource> readable;
        try
        {
            readable = await MemorySourceAccess.ReadableAsync(
                access, links, witnesses.Select(SourceOf).OfType<MemorySource>(), viewer.UserId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not check memory sources; {Outcome}", "listing no spaces");
            return null;
        }

        List<string> hidden = [];
        foreach (IGrouping<Guid, WitnessRow> witness in witnesses.GroupBy(w => w.Container))
        {
            if (witness.All(w => SourceOf(w) is not { } source || readable.Contains(source)))
                holding.Add(witness.Key.ToString());
            else
                hidden.Add(witness.Key.ToString());
        }
        if (hidden.Count == 0)
            return holding;

        // A hidden witness says nothing about the space's other facts, so those spaces are tallied in full.
        IReadOnlyDictionary<string, MemorySpaceTally>? tallied = await TallyAsync(viewer, hidden, ct);
        if (tallied is null)
            return null;
        holding.UnionWith(tallied.Where(t => t.Value.Facts > 0).Select(t => t.Key));
        return holding;
    }

    public async Task<Container?> CreatePersonalAsync(MemoryCaller owner, string name, string? description, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        (Guid? ownerUser, Guid? ownerAgent) = OwnerOf(owner);
        if (ownerUser is null && ownerAgent is null)
            throw new ArgumentException("A personal space needs a user or an agent to belong to.", nameof(owner));
        name = name.Trim();
        if (!PathUtilities.IsValidContainerName(name))
            throw new ArgumentException($"'{name}' is not a valid space name.", nameof(name));

        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        Guid id = Guid.NewGuid();
        DateTime now = clock.GetUtcNow().UtcDateTime;
        string? described = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        // Insert-or-nothing: a name the owner already uses comes back as no row rather than a unique-index failure.
        int inserted = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO containers (id, name, description, kind, audience, owner_user_id, owner_agent_id, created_at, updated_at)
            VALUES ({id}, {name}, {described}, 'memory', 'personal', {ownerUser}, {ownerAgent}, {now}, {now})
            ON CONFLICT (owner_user_id, owner_agent_id, name) WHERE audience = 'personal' DO NOTHING
            """, ct);
        if (inserted == 0)
            return null;

        return new Container(id.ToString(), name, described, now, now, Kind: ContainerKind.Memory, Audience: MemoryScope.Personal)
        {
            OwnerUserId = ownerUser,
            OwnerAgentId = ownerAgent,
        };
    }

    public async Task<MemorySpaceImpact?> ImpactAsync(Guid containerId, MemoryCaller viewer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        DateTimeOffset now = clock.GetUtcNow();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        IReadOnlySet<MemorySource>? readable = await ReadableAsync(
            context, viewer, CitedInSpace,
            [.. Viewer(viewer, now), new NpgsqlParameter("container", NpgsqlDbType.Uuid) { Value = containerId }],
            "reporting no delete impact", ct);
        if (readable is null)
            return null;

        // Every version, current or not, of the facts the viewer may read: the same rows the space's history shows them.
        ImpactRow row = await context.Database.SqlQueryRaw<ImpactRow>(
            $"""
            SELECT count(DISTINCT f.chain_id) AS "Facts", count(*) AS "Versions",
                   count(DISTINCT COALESCE(f.author_user_id, f.author_agent_id)) AS "Authors"
            FROM memory_facts f
            WHERE f.container_id = @container AND {FactScopeVisible} AND {SourcesReadable}
            """,
            [.. Viewer(viewer, now), new NpgsqlParameter("container", NpgsqlDbType.Uuid) { Value = containerId }, .. Readable(readable)])
            .SingleAsync(ct);
        return new MemorySpaceImpact((int)row.Facts, (int)row.Versions, (int)row.Authors);
    }

    /// <summary>
    /// Which sources the viewer may read among those <paramref name="citedSql"/> selects: the current facts in their
    /// scope in some spaces, or every fact in their scope in one. Null when access could not be checked.
    /// </summary>
    private async Task<IReadOnlySet<MemorySource>?> ReadableAsync(
        KnowledgeDbContext context, MemoryCaller viewer, string citedSql, NpgsqlParameter[] parameters, string onFailure,
        CancellationToken ct)
    {
        List<SourceRow> cited = await context.Database.SqlQueryRaw<SourceRow>(citedSql, [.. parameters]).ToListAsync(ct);
        try
        {
            return await MemorySourceAccess.ReadableAsync(
                access, links, cited.Select(r => new MemorySource((MemorySourceKind)r.Kind, r.Ref)), viewer.UserId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not check memory sources; {Outcome}", onFailure);
            return null;
        }
    }

    public async Task StarAsync(Guid containerId, MemoryCaller caller, CancellationToken ct = default)
    {
        (Guid? user, Guid? agent) = Owner(caller);
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO memory_space_stars (id, container_id, user_id, agent_id, starred_at)
            VALUES ({Guid.NewGuid()}, {containerId}, {user}, {agent}, {clock.GetUtcNow()})
            ON CONFLICT (container_id, user_id, agent_id) DO NOTHING
            """, ct);
    }

    public async Task UnstarAsync(Guid containerId, MemoryCaller caller, CancellationToken ct = default)
    {
        (Guid? user, Guid? agent) = Owner(caller);
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        await context.MemorySpaceStars
            .Where(s => s.ContainerId == containerId && s.UserId == user && s.AgentId == agent)
            .ExecuteDeleteAsync(ct);
    }

    private const string CitedSources = """
        SELECT DISTINCT s.kind AS "Kind", s.ref AS "Ref"
        FROM memory_fact_sources s
        JOIN memory_facts f ON f.id = s.fact_id
        JOIN containers c ON c.id = f.container_id
        """;

    // The space's name or description contains @pattern (already LIKE-escaped), or there is no pattern.
    private const string SpaceMatches = "(@pattern IS NULL OR c.name ILIKE @pattern OR c.description ILIKE @pattern)";

    private const string CitedInVisibleSpaces =
        $"{CitedSources} WHERE {FactCurrent} AND {ContainerVisible} AND {SpaceMatches} AND {FactScopeVisible}";

    // As above, but for facts current or not, when ended facts can list a space.
    private const string CitedEverInVisibleSpaces =
        $"{CitedSources} WHERE {ContainerVisible} AND {SpaceMatches} AND {FactScopeVisible}";

    private const string CitedInSpaces =
        $"{CitedSources} WHERE f.container_id = ANY(@containers) AND {FactCurrent} AND {ContainerVisible} AND {FactScopeVisible}";

    private const string CitedInSpace = $"{CitedSources} WHERE f.container_id = @container AND {FactScopeVisible}";

    // Every source the fact cites is one the viewer may read (@documents and @nodes, from ReadableAsync).
    private const string SourcesReadable =
        "NOT EXISTS (SELECT 1 FROM memory_fact_sources s WHERE s.fact_id = f.id "
        + "AND NOT ((s.kind = 0 AND s.ref = ANY(@documents)) OR (s.kind = 1 AND s.ref = ANY(@nodes))))";

    private static NpgsqlParameter[] Readable(IReadOnlySet<MemorySource> readable) =>
    [
        new("documents", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = readable.Where(s => s.Kind == MemorySourceKind.Document).Select(s => s.Ref).ToArray(),
        },
        new("nodes", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = readable.Where(s => s.Kind == MemorySourceKind.Node).Select(s => s.Ref).ToArray(),
        },
    ];

    private static NpgsqlParameter[] Viewer(MemoryCaller viewer, DateTimeOffset now) =>
    [
        new("user", NpgsqlDbType.Uuid) { Value = (object?)viewer.UserId ?? DBNull.Value },
        new("agent", NpgsqlDbType.Uuid) { Value = (object?)viewer.AgentId ?? DBNull.Value },
        new("now", NpgsqlDbType.TimestampTz) { Value = now },
    ];

    private static NpgsqlParameter Containers(Guid[] ids) =>
        new("containers", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids };

    /// <summary>The ids that parse as GUIDs, once each; anything else names no space.</summary>
    private static Guid[] Ids(IReadOnlyCollection<string> containerIds) =>
        [.. containerIds.Select(id => Guid.TryParse(id, out Guid g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct()];

    /// <summary>The container's own change, or the newest fact when that came later.</summary>
    private static DateTimeOffset Later(DateTimeOffset updatedAt, DateTimeOffset? newest) =>
        newest is { } n && n > updatedAt ? n : updatedAt;

    /// <summary>Who a star or personal space belongs to: the user, or the agent when it acts for no user.</summary>
    private static (Guid? User, Guid? Agent) OwnerOf(MemoryCaller caller) =>
        caller.UserId is { } user ? (user, null) : (null, caller.AgentId);

    private static (Guid? User, Guid? Agent) Owner(MemoryCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        (Guid? user, Guid? agent) = OwnerOf(caller);
        return user is null && agent is null
            ? throw new ArgumentException("A star needs a user or an agent to belong to.", nameof(caller))
            : (user, agent);
    }

    /// <summary>The text as a literal inside an ILIKE pattern: its own wildcards and the escape character matched as themselves.</summary>
    internal static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private sealed class SourceRow
    {
        public short Kind { get; set; }

        public string Ref { get; set; } = "";
    }

    private sealed class SpaceRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public string? Description { get; set; }

        public string Kind { get; set; } = "";

        public string Audience { get; set; } = "";

        public Guid? OwnerUserId { get; set; }

        public Guid? OwnerAgentId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public long Facts { get; set; }

        public DateTimeOffset? Newest { get; set; }

        public bool Starred { get; set; }
    }

    private sealed class CandidateRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public string? Description { get; set; }

        public string Kind { get; set; } = "";

        public string Audience { get; set; } = "";

        public Guid? OwnerUserId { get; set; }

        public Guid? OwnerAgentId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public long FactsAtLeast { get; set; }

        public long FactsAtMost { get; set; }

        public DateTimeOffset? NewestAtLeast { get; set; }

        public DateTimeOffset? NewestAtMost { get; set; }

        public bool Starred { get; set; }
    }

    private sealed class TallyRow
    {
        public Guid Id { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public long Facts { get; set; }

        public DateTimeOffset? Newest { get; set; }
    }

    private sealed class WitnessRow
    {
        public Guid Container { get; set; }

        public short? Kind { get; set; }

        public string? Ref { get; set; }
    }

    private sealed class ImpactRow
    {
        public long Facts { get; set; }

        public long Versions { get; set; }

        public long Authors { get; set; }
    }
}

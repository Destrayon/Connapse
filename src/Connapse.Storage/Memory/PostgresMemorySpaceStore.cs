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
/// a viewer only when it is team or theirs and they can read every source it cites, checked once per list for every
/// source in play and passed into one grouped query. A delete's impact counts by the same rule, so it never reveals
/// facts the viewer can't read.
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

    public async Task<IReadOnlyList<MemorySpaceSummary>?> ListAsync(MemoryCaller viewer, string? text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        DateTimeOffset now = clock.GetUtcNow();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);

        string? pattern = string.IsNullOrWhiteSpace(text) ? null : "%" + EscapeLike(text.Trim()) + "%";
        // Only the spaces the text matches have their sources checked, so a narrow search stays cheap.
        IReadOnlySet<MemorySource>? readable = await ReadableAsync(context, viewer, now, null, pattern, "listing no spaces", ct);
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
              AND (c.kind = 'memory' OR COALESCE(a.facts, 0) > 0)
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
                r.Newest is { } newest && newest > r.UpdatedAt ? newest : r.UpdatedAt,
                r.Starred))
            .ToList();
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
        IReadOnlySet<MemorySource>? readable = await ReadableAsync(context, viewer, now, containerId, null, "reporting no delete impact", ct);
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
    /// Which sources the viewer may read among those cited by facts in scope for them: every fact in one space when
    /// <paramref name="containerId"/> is given, else the current facts in every space they may see. Null when access
    /// could not be checked. A <paramref name="pattern"/> narrows the spaces to those whose name or description match.
    /// </summary>
    private async Task<IReadOnlySet<MemorySource>?> ReadableAsync(
        KnowledgeDbContext context, MemoryCaller viewer, DateTimeOffset now, Guid? containerId, string? pattern,
        string onFailure, CancellationToken ct)
    {
        List<SourceRow> cited = await context.Database.SqlQueryRaw<SourceRow>(
            containerId is null ? CitedInVisibleSpaces : CitedInSpace,
            [
                .. Viewer(viewer, now),
                new NpgsqlParameter("container", NpgsqlDbType.Uuid) { Value = containerId ?? Guid.Empty },
                new NpgsqlParameter("pattern", NpgsqlDbType.Text) { Value = (object?)pattern ?? DBNull.Value },
            ])
            .ToListAsync(ct);
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

    private sealed class ImpactRow
    {
        public long Facts { get; set; }

        public long Versions { get; set; }

        public long Authors { get; set; }
    }
}

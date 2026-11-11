using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Memory;
using Connapse.Storage.Tables;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// CON-309: a memory container's facts read as a built-in table through the restricted table-reader login and the
/// <c>tables_memory.facts</c> view, with no data copied. Random facts over random permissions are read through the
/// table service now, as of a past time and as changes, and each answer must hold exactly the facts memory's own rule
/// allows: every source readable, and team or the reader's own. The login itself reads nothing without a token, nothing
/// of another container, and never the memory tables underneath.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemoryTableTests(SharedWebAppFixture fixture)
{
    private sealed class FakeAccess(Dictionary<Guid, HashSet<string>> readable) : IDocumentAccess
    {
        public Task<IReadOnlySet<string>> ReadableAsync(Guid? userId, IReadOnlyCollection<string> documentIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(userId is { } u && readable.TryGetValue(u, out HashSet<string>? r)
                ? documentIds.Where(r.Contains).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed class NoLinks : ILinkIndex
    {
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<ILookup<string, string>> NodesOfAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ILookup<string, string>> DocumentsOfAsync(IReadOnlyCollection<string> nodeIds, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2));
    }

    /// <summary>A fact as planted, with what the expected answers are worked out from.</summary>
    private sealed record Planted(Guid Id, Guid Author, MemoryScope Scope, string[] Sources,
        DateTimeOffset? From, DateTimeOffset? To, DateTimeOffset? Superseded);

    private static readonly int?[] FromDays = [null, -10, -2, 5];
    private static readonly int?[] ToDays = [null, -5, -1, 3, 20];

    [Fact]
    public async Task Read_RandomFactsAndPermissions_ShowsExactlyWhatMemorysRuleAllowsNowThenAndAsChanges()
    {
        int seed = Environment.TickCount;
        var random = new Random(seed);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var tables = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int sample = 0; sample < 12; sample++)
        {
            Guid[] users = [Guid.NewGuid(), Guid.NewGuid()];
            string[] documents = [.. Enumerable.Range(0, 3).Select(_ => Guid.NewGuid().ToString())];
            Dictionary<Guid, HashSet<string>> readable = users.ToDictionary(u => u,
                _ => documents.Where(_ => random.Next(2) == 0).ToHashSet(StringComparer.Ordinal));
            List<Planted> planted = [.. Enumerable.Range(0, random.Next(0, 10)).Select(_ =>
            {
                int? from = FromDays[random.Next(FromDays.Length)];
                int? to = ToDays[random.Next(ToDays.Length)];
                if (from is { } f && to is { } t && t <= f)
                    to = null;
                return new Planted(Guid.NewGuid(), users[random.Next(2)], random.Next(2) == 0 ? MemoryScope.Personal : MemoryScope.Team,
                    [.. documents.Where(_ => random.Next(3) == 0)],
                    from is { } fd ? now.AddDays(fd) : null, to is { } td ? now.AddDays(td) : null,
                    random.Next(4) == 0 ? now.AddDays(-3) : null);
            })];
            Guid reader = users[random.Next(2)];
            DateTimeOffset then = now.AddDays(-4);
            (DateTimeOffset since, DateTimeOffset until) = (now.AddDays(-6), now.AddHours(-12));

            (Guid container, IContainerStore containers) = await NewSpaceAsync(scope, planted);
            try
            {
                var service = new MemoryService(new PostgresMemoryStore(Factory(scope), TimeProvider.System), new FakeAccess(readable),
                    new NoLinks(), Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);
                MemoryViewer viewer = (await service.ViewerOfAsync(container, new MemoryCaller(reader, null, "r")))!;
                TableAccess access = new(new HashSet<Guid>()) { Memory = viewer };

                bool Visible(Planted p) => p.Sources.All(readable[reader].Contains) && (p.Scope == MemoryScope.Team || p.Author == reader);
                static bool HoldsAt(Planted p, DateTimeOffset at) =>
                    p.Superseded is null && (p.From is null || p.From <= at) && (p.To is null || p.To > at);
                static bool Within(DateTimeOffset? at, DateTimeOffset since, DateTimeOffset until) => at >= since && at <= until;

                TableQueryResult current = await tables.ReadAsync(container, access, new TableReadRequest(MemoryTable.Id, ["fact"], Limit: 100));
                TableQueryResult past = await tables.ReadAsync(container, access,
                    new TableReadRequest(MemoryTable.Id, ["fact"], Limit: 100, SeenAsOf: then));
                TableQueryResult changes = await tables.ChangesAsync(container, access,
                    new TableChangesRequest(MemoryTable.Id, since, until, ["fact"], Limit: 100));

                string because = $"seed {seed}, sample {sample}";
                Ids(current, 0).Should().BeEquivalentTo(planted.Where(p => Visible(p) && HoldsAt(p, DateTimeOffset.UtcNow)).Select(p => p.Id), because);
                Ids(past, 0).Should().BeEquivalentTo(planted.Where(p => Visible(p) && HoldsAt(p, then)).Select(p => p.Id), because);
                changes.Columns.Should().Equal("change", "fact");
                Ids(changes, 1).Should().BeEquivalentTo(planted
                    .Where(p => Visible(p) && (Within(p.From, since, until) || Within(p.To, since, until))).Select(p => p.Id), because);
            }
            finally
            {
                await DeleteSpaceAsync(scope, containers, container);
            }
        }
    }

    [Fact]
    public async Task ReaderLogin_WithoutATokenOrWithAnotherContainersToken_SeesNoFactsAndNeverTheTablesUnderneath()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid author = Guid.NewGuid();
        Planted team = new(Guid.NewGuid(), author, MemoryScope.Team, [], null, null, null);
        (Guid mine, IContainerStore containers) = await NewSpaceAsync(scope, [team]);
        (Guid other, _) = await NewSpaceAsync(scope, [team with { Id = Guid.NewGuid() }]);
        try
        {
            MemoryViewer viewer = new(new MemoryCaller(author, null, "a"), new HashSet<MemorySource>());
            await Login.EnsureMemoryGrantsAsync(mine, default);
            string mineToken = await Login.IssueMemoryTokenAsync(mine, viewer, TimeSpan.FromMinutes(1), default);
            string otherToken = await Login.IssueMemoryTokenAsync(other, viewer, TimeSpan.FromMinutes(1), default);

            (await CountAsync(mine, mineToken, "tables_memory.facts")).Should().Be(1);
            (await CountAsync(mine, token: null, "tables_memory.facts")).Should().Be(0, "no token shows nothing");
            (await CountAsync(mine, otherToken, "tables_memory.facts")).Should().Be(1,
                "another container's token shows only that container's facts, never this one's twice");
            (await CountAsync(mine, otherToken, $"tables_memory.facts WHERE c_1 = '{team.Id}'")).Should().Be(0);

            foreach (string underneath in new[] { "public.memory_facts", "public.memory_fact_sources", "tables_auth.memory_requests" })
            {
                Func<Task> read = () => CountAsync(mine, mineToken, underneath);
                (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501", underneath);
            }
        }
        finally
        {
            await DeleteSpaceAsync(scope, containers, mine);
            await DeleteSpaceAsync(scope, containers, other);
        }
    }

    private TableReaderLogin Login => fixture.Factory.Services.GetRequiredService<TableReaderLogin>();

    private static IDbContextFactory<KnowledgeDbContext> Factory(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();

    private static IEnumerable<Guid> Ids(TableQueryResult result, int column) => result.Rows.Select(r => Guid.Parse((string)r[column]!));

    /// <summary>Counts rows of <paramref name="from"/> on the reader login as the container's role.</summary>
    private async Task<long> CountAsync(Guid containerId, string? token, string from)
    {
        await using NpgsqlConnection connection = await Login.OpenConnectionAsync(default);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (NpgsqlCommand role = new($"SET LOCAL ROLE {TableDdl.Quote(Login.ContainerRoleName(containerId))}", connection, transaction))
            await role.ExecuteNonQueryAsync();
        if (token is not null)
        {
            await using NpgsqlCommand bind = new("SELECT pg_catalog.set_config('tables_auth.token', $1, true)", connection, transaction);
            bind.Parameters.Add(new NpgsqlParameter { Value = token });
            await bind.ExecuteNonQueryAsync();
        }
        await using NpgsqlCommand count = new($"SELECT count(*) FROM {from}", connection, transaction);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    /// <summary>A new memory space holding <paramref name="facts"/>, written straight to its tables.</summary>
    private static async Task<(Guid Id, IContainerStore Containers)> NewSpaceAsync(AsyncServiceScope scope, IReadOnlyList<Planted> facts)
    {
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        Container space = await containers.CreateAsync(new CreateContainerRequest($"memtable-{Guid.NewGuid():N}", null, ContainerKind.Memory));
        Guid id = Guid.Parse(space.Id);
        await using KnowledgeDbContext db = await Factory(scope).CreateDbContextAsync();
        db.MemoryFacts.AddRange(facts.Select(p => new MemoryFactEntity
        {
            Id = p.Id,
            ChainId = p.Id,
            ContainerId = id,
            Subject = "s",
            Attribute = "a",
            Value = "v",
            ValidFrom = p.From,
            ValidTo = p.To,
            SupersededAt = p.Superseded,
            RecordedAt = DateTimeOffset.UtcNow.AddDays(-30),
            AuthorUserId = p.Author,
            AuthorName = "author",
            Scope = (short)p.Scope,
            Sources = [.. p.Sources.Select((s, i) => new MemoryFactSourceEntity { FactId = p.Id, Ordinal = i, Kind = 0, Ref = s })],
        }));
        await db.SaveChangesAsync();
        return (id, containers);
    }

    private static async Task DeleteSpaceAsync(AsyncServiceScope scope, IContainerStore containers, Guid id)
    {
        await using (KnowledgeDbContext db = await Factory(scope).CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("DELETE FROM memory_facts WHERE container_id = {0}", id);
        await containers.DeleteAsync(id);
    }
}

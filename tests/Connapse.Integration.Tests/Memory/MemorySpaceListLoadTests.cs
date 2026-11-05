using System.Diagnostics;
using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Memory;
using IOperationAuthorizer = Connapse.Application.Ports.IOperationAuthorizer;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// CON-243: the Memory page's unfiltered spaces list at 200 spaces and 50,000 memories. Its own database, so nothing
/// another test made is listed. A typical reader may read every source; the worst case is a reader who may read none,
/// so every documents container's witness is denied and the whole container is checked. Each timed call is the best of
/// three after a warm-up.
/// </summary>
[Trait("Category", "Gate")]
public class MemorySpaceListLoadTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int TeamSpaces = 180;
    private const int DocumentContainers = 20;
    private const int Facts = 50_000;

    // Five facts in the same space cite each document, so each space cites 50 of its own and 50,000 facts cite 10,000.
    private const int FactsPerDocument = 5;
    private const int DocumentsPerSpace = Facts / (TeamSpaces + DocumentContainers) / FactsPerDocument;

    private const int PageSize = 50;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_space_load")
        .WithUsername("space_load")
        .WithPassword("space_load")
        .WithCreateParameterModifier(TestContainerLimits.Apply)
        .Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private DbContextOptions<KnowledgeDbContext> Options() => new DbContextOptionsBuilder<KnowledgeDbContext>()
        .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseVector())
        .Options;

    /// <summary>Counts every document it is asked about; reads all of them, or none.</summary>
    private sealed class CountingAccess(bool readsAll) : IDocumentAccess
    {
        public int Asked { get; set; }

        public Task<IReadOnlySet<string>> ReadableAsync(Guid? userId, IReadOnlyCollection<string> documentIds, CancellationToken ct = default)
        {
            Asked += documentIds.Count;
            return Task.FromResult<IReadOnlySet<string>>(readsAll
                ? documentIds.ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal));
        }
    }

    private sealed class NoLinks : ILinkIndex
    {
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<ILookup<string, string>> NodesOfAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ILookup<string, string>> DocumentsOfAsync(IReadOnlyCollection<string> nodeIds, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2));
    }

    private async Task SeedAsync()
    {
        await using KnowledgeDbContext context = new(Options());
        await context.GetService<IMigrator>().MigrateAsync();
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        DateTime then = DateTime.UtcNow.AddDays(-30);
        for (int i = 0; i < TeamSpaces + DocumentContainers; i++)
        {
            context.Containers.Add(new ContainerEntity
            {
                Id = Guid.NewGuid(),
                Name = $"space-{i:D3}",
                Kind = i < TeamSpaces ? ContainerKind.Memory : ContainerKind.Documents,
                CreatedAt = then,
                UpdatedAt = then,
            });
        }
        await context.SaveChangesAsync();

        // Team facts spread evenly over the 200 spaces, each citing one document, recorded over the last day.
        await context.Database.ExecuteSqlRawAsync(
            """
            WITH spaces AS (SELECT id, row_number() OVER (ORDER BY name) - 1 AS n FROM containers),
                 facts AS (SELECT g, gen_random_uuid() AS id FROM generate_series(0, {0} - 1) g)
            INSERT INTO memory_facts (id, chain_id, container_id, subject, attribute, value, recorded_at, author_user_id, scope)
            SELECT f.id, f.id, s.id, 'subject ' || f.g % 500, 'attribute ' || f.g % 7, 'value ' || f.g,
                   now() - (f.g || ' seconds')::interval, {1}, 1
            FROM facts f JOIN spaces s ON s.n = f.g % {2}
            """, Facts, Guid.NewGuid(), TeamSpaces + DocumentContainers);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO memory_fact_sources (fact_id, ordinal, kind, ref)
            SELECT f.id, 0, 0, md5(f.container_id::text || ' ' || (row_number() OVER (PARTITION BY f.container_id ORDER BY f.id) - 1) / {0})
            FROM memory_facts f
            """, FactsPerDocument);
        await context.Database.ExecuteSqlRawAsync("ANALYZE");
    }

    private async Task<(TimeSpan Best, int Asked, MemorySpacePage Page)> ListAsync(bool readsAll, MemorySpaceSort sort)
    {
        OperationContext context = new(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")), OperationSurface.Rest);
        IOperationAuthorizer authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);

        (TimeSpan Best, int Asked, MemorySpacePage Page) best = (TimeSpan.MaxValue, 0, null!);
        for (int i = 0; i < 4; i++)
        {
            CountingAccess access = new(readsAll);
            ListMemorySpaces list = new(
                new PostgresMemorySpaceStore(new PooledDbContextFactory<KnowledgeDbContext>(Options()), access, new NoLinks(),
                    TimeProvider.System, NullLogger<PostgresMemorySpaceStore>.Instance),
                authorizer);
            Stopwatch timer = Stopwatch.StartNew();
            Attempt<MemorySpacePage, MemorySpaceStatus> page = await list.RunAsync(context, new MemorySpaceQuery(Sort: sort, Limit: PageSize));
            timer.Stop();
            page.Succeeded.Should().BeTrue();
            // The first call warms the pool and the JIT.
            if (i > 0 && timer.Elapsed < best.Best)
                best = (timer.Elapsed, access.Asked, page.Result!);
        }
        return best;
    }

    [Fact]
    public async Task UnfilteredList_200SpacesAnd50kMemories_ChecksOnlyThePage()
    {
        await SeedAsync();

        foreach (MemorySpaceSort sort in Enum.GetValues<MemorySpaceSort>())
        {
            (TimeSpan typical, int typicalAsked, MemorySpacePage page) = await ListAsync(readsAll: true, sort);
            (TimeSpan worst, int worstAsked, MemorySpacePage hidden) = await ListAsync(readsAll: false, sort);
            output.WriteLine($"sort={sort}: typical {typical.TotalMilliseconds:F0} ms, {typicalAsked} documents checked; "
                + $"worst {worst.TotalMilliseconds:F0} ms, {worstAsked} documents checked");

            page.Total.Should().Be(TeamSpaces + DocumentContainers);
            page.Counts.Should().Be(new MemorySpaceCounts(TeamSpaces + DocumentContainers, 0, 0, TeamSpaces, DocumentContainers));
            page.Items.Should().HaveCount(PageSize).And.OnlyContain(v => v.Facts == Facts / (TeamSpaces + DocumentContainers));
            hidden.Counts.Should().Be(new MemorySpaceCounts(TeamSpaces, 0, 0, TeamSpaces, 0),
                "a documents container whose every fact cites a hidden document isn't listed");
            hidden.Items.Should().OnlyContain(v => v.Facts == 0);

            // A typical reader: the page's documents, and one per documents container to show it holds a readable fact.
            typicalAsked.Should().Be(PageSize * DocumentsPerSpace + DocumentContainers);
            // A reader who may read nothing: every witness is denied, so each documents container is checked in full.
            // By name the page is still all that's counted; by facts or updated every space ties at nothing, so all are.
            int listedSpaces = sort == MemorySpaceSort.Name ? PageSize : TeamSpaces;
            worstAsked.Should().Be(DocumentContainers + DocumentContainers * DocumentsPerSpace + listedSpaces * DocumentsPerSpace);
        }
    }
}

using System.Data.Common;
using Connapse.Core;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// A recall across containers ranks containers that use different words. A forget committed while it runs must not
/// shorten the page: every container is ranked and loaded in one statement, so it returns what memory held when it began.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemoryRecallSnapshotTests(SharedWebAppFixture fixture)
{
    /// <summary>Runs <see cref="Between"/> once, on its own connection, right after the first ranking statement.</summary>
    private sealed class AfterRanking : DbCommandInterceptor
    {
        public Func<Task>? Between { get; set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Between is { } between && command.CommandText.Contains("ts_rank", StringComparison.Ordinal))
            {
                Between = null;
                await between();
            }
            return result;
        }
    }

    private sealed class Factory(DbContextOptions<KnowledgeDbContext> options) : IDbContextFactory<KnowledgeDbContext>
    {
        public KnowledgeDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task QueryAsync_AcrossContainersWithAForgetAfterRanking_ReturnsTheFullPage()
    {
        string connection = fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!;
        DbContextOptions<KnowledgeDbContext> Options(params IInterceptor[] interceptors) => new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.UseVector())
            .AddInterceptors(interceptors)
            .Options;
        var afterRanking = new AfterRanking();
        var store = new PostgresMemoryStore(new Factory(Options()), TimeProvider.System);
        var watched = new PostgresMemoryStore(new Factory(Options(afterRanking)), TimeProvider.System);

        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await using (var db = new KnowledgeDbContext(Options()))
        {
            db.Containers.Add(new ContainerEntity { Id = a, Name = $"snapshot-a-{a:N}", Kind = ContainerKind.Memory });
            db.Containers.Add(new ContainerEntity { Id = b, Name = $"snapshot-b-{b:N}", Kind = ContainerKind.Memory });
            await db.SaveChangesAsync();
        }
        var caller = new MemoryCaller(Guid.NewGuid(), null, "snapshot");
        // Every fact of A says "deploy" and only one of B's does, so the two containers rank under different plans.
        MemoryFact inA = await store.AddAsync(a, new NewMemoryFact("pipeline", "step", "deploy one", null, null, []), caller);
        await store.AddAsync(a, new NewMemoryFact("pipeline", "step", "deploy two", null, null, []), caller);
        MemoryFact inB = await store.AddAsync(b, new NewMemoryFact("pipeline", "step", "deploy three", null, null, []), caller);
        await store.AddAsync(b, new NewMemoryFact("pipeline", "step", "release four", null, null, []), caller);
        await store.AddAsync(b, new NewMemoryFact("pipeline", "step", "release five", null, null, []), caller);

        // Each container loses a fact once ranking has run; a later statement for either would miss it.
        afterRanking.Between = async () =>
        {
            (await store.ForgetAsync(inA.Id, _ => Task.FromResult(true))).Should().Be(1);
            (await store.ForgetAsync(inB.Id, _ => Task.FromResult(true))).Should().Be(1);
        };
        IReadOnlyList<MemoryFact> page = await watched.QueryAsync(
            new MemoryQuery(Guid.Empty, "deploy", ContainerIds: [a, b]), skip: 0, take: 3);

        afterRanking.Between.Should().BeNull("the forgets ran after ranking");
        page.Should().HaveCount(3);
        page.Should().ContainSingle(f => f.Id == inB.Id);
        (await store.GetAsync(inA.Id)).Should().BeNull();
    }
}

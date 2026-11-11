using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-305: a read or an aggregate joins one other table of the same container on named columns. The joined table is
/// trimmed like the queried one: a table the caller can't read is missing, a hidden column doesn't exist, and a merged
/// joined table matches only its readable rows.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class TableJoinTests(SharedWebAppFixture fixture)
{
    private static readonly TableColumn[] SalesColumns =
    [
        new("Region", TableColumnType.Text),
        new("Amount", TableColumnType.Number),
    ];

    private static readonly TableColumn[] RegionColumns =
    [
        new("Region", TableColumnType.Text),
        new("Manager", TableColumnType.Text),
    ];

    [Fact]
    public async Task ReadAsync_InnerJoin_ReturnsOnlyMatchedRowsWithJoinedColumns()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Setup setup = await NewAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            TableQueryResult result = await service.ReadAsync(setup.ContainerId, setup.Access(), new TableReadRequest(
                setup.Sales.Id, ["Region", "Amount", "r.Manager"],
                OrderBy: [new TableOrder(new TableField("Amount"), TableSortDirection.Ascending)],
                Join: JoinRegions(setup)));

            result.Columns.Should().Equal("Region", "Amount", "r.Manager");
            result.Rows.Select(r => (r[0], r[1], r[2])).Should().Equal(
                ("south", (object)5m, (object)"bob"), ("north", 10m, "alice"), ("north", 20m, "alice"));
            result.Notice.Should().Be(TableQueryResult.UntrustedDataNotice);
        }
        finally
        {
            await DeleteAsync(scope, setup);
        }
    }

    [Fact]
    public async Task ReadAndAggregate_LeftJoin_KeepUnmatchedRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Setup setup = await NewAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            TableJoin left = JoinRegions(setup) with { Kind = TableJoinKind.Left };

            TableQueryResult rows = await service.ReadAsync(setup.ContainerId, setup.Access(), new TableReadRequest(
                setup.Sales.Id, ["Region", "r.Manager"], Where: new TableCondition(new TableField("Region"), TableFilterOperator.Equal, ["east"]),
                Join: left));
            TableQueryResult totals = await service.AggregateAsync(setup.ContainerId, setup.Access(), new TableAggregateRequest(
                setup.Sales.Id, [new TableField("Amount", TableAggregateFunction.Sum)], ["r.Manager"],
                OrderBy: [new TableOrder(new TableField("Amount", TableAggregateFunction.Sum), TableSortDirection.Descending)],
                Join: left));

            rows.Rows.Should().ContainSingle().Which.Should().Equal(new object?[] { "east", null });
            totals.Columns.Should().Equal("r.Manager", "sum(Amount)");
            totals.Rows.Select(r => (r[0], r[1])).Should().Equal((null, (object)41m), ("alice", 30m), ("bob", 5m));
        }
        finally
        {
            await DeleteAsync(scope, setup);
        }
    }

    [Fact]
    public async Task ReadAndAggregate_JoinedTableTheCallerCantRead_IsNotFound()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Setup setup = await NewAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            TableAccess salesOnly = new(new HashSet<Guid> { setup.SalesDocument });

            Func<Task> read = () => service.ReadAsync(setup.ContainerId, salesOnly,
                new TableReadRequest(setup.Sales.Id, Join: JoinRegions(setup)));
            Func<Task> aggregate = () => service.AggregateAsync(setup.ContainerId, salesOnly, new TableAggregateRequest(
                setup.Sales.Id, [new TableField(null, TableAggregateFunction.Count)], Join: JoinRegions(setup)));

            (await read.Should().ThrowAsync<TableQueryException>()).WithMessage($"There is no table with id {setup.Regions.Id} in this container.")
                .Which.Message.Should().NotContain("Manager", "an unreadable table's columns are never named");
            await aggregate.Should().ThrowAsync<TableQueryException>().WithMessage($"*no table with id {setup.Regions.Id}*");
        }
        finally
        {
            await DeleteAsync(scope, setup);
        }
    }

    [Fact]
    public async Task ReadAsync_JoinedTableOfAnotherContainer_IsNotFound()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Setup setup = await NewAsync(scope);
        Setup other = await NewAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            TableAccess both = new(new HashSet<Guid> { setup.SalesDocument, other.RegionsDocument });

            Func<Task> act = () => service.ReadAsync(setup.ContainerId, both,
                new TableReadRequest(setup.Sales.Id, Join: JoinRegions(other)));

            await act.Should().ThrowAsync<TableQueryException>().WithMessage($"*no table with id {other.Regions.Id}*");
        }
        finally
        {
            await DeleteAsync(scope, setup);
            await DeleteAsync(scope, other);
        }
    }

    [Fact]
    public async Task ReadAsync_HiddenJoinColumn_ReadsAsMissing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Setup setup = await NewAsync(scope);
        try
        {
            await scope.ServiceProvider.GetRequiredService<ITableStore>()
                .SetHiddenColumnsAsync(setup.ContainerId, setup.Regions.Id, ["Region"]);
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            Func<Task> act = () => service.ReadAsync(setup.ContainerId, setup.Access(),
                new TableReadRequest(setup.Sales.Id, Join: JoinRegions(setup)));

            await act.Should().ThrowAsync<TableQueryException>()
                .WithMessage("Table \"Regions\" has no column \"r.Region\". Its columns are: \"r.Manager\".");
        }
        finally
        {
            await DeleteAsync(scope, setup);
        }
    }

    [Fact]
    public async Task ReadAsync_JoinColumnsOfDifferentTypes_SaysBothTypes()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Setup setup = await NewAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            Func<Task> act = () => service.ReadAsync(setup.ContainerId, setup.Access(),
                new TableReadRequest(setup.Sales.Id, Join: JoinRegions(setup) with { Column = "Amount" }));

            await act.Should().ThrowAsync<TableQueryException>().WithMessage("Can't join \"Amount\", a number column, to \"r.Region\", a text column*");
        }
        finally
        {
            await DeleteAsync(scope, setup);
        }
    }

    [Fact]
    public async Task ReadAsync_MergedJoinedTable_MatchesOnlyReadableRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Setup setup = await NewAsync(scope);
        try
        {
            Guid open = await NewDocumentAsync(scope, setup.ContainerId), secret = await NewDocumentAsync(scope, setup.ContainerId);
            TableInfo merged = await scope.ServiceProvider.GetRequiredService<ITableStore>().CreateAsync(setup.ContainerId, new TableLoad(
                "Owners", RegionColumns,
                Rows([["north", "olivia", open], ["east", "mallory", secret]]),
                SourceDocuments: [open, secret]));
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            TableAccess access = new(new HashSet<Guid> { setup.SalesDocument, open });
            TableJoin join = new(merged.Id, "o", "Region", "Region", TableJoinKind.Left);

            TableQueryResult result = await service.ReadAsync(setup.ContainerId, access, new TableReadRequest(
                setup.Sales.Id, ["Region", "o.Manager"],
                OrderBy: [new TableOrder(new TableField("Amount"), TableSortDirection.Ascending)], Join: join));

            result.Rows.Select(r => (r[0], r[1])).Should().Equal(
                (null, null), ("south", null), ("north", (object)"olivia"), ("north", "olivia"), ("east", null));
            result.Rows.Should().NotContain(r => Equals(r[1], "mallory"), "the secret document's row matches nothing");
        }
        finally
        {
            await DeleteAsync(scope, setup);
        }
    }

    private sealed record Setup(IContainerStore Containers, Guid ContainerId, Guid SalesDocument, Guid RegionsDocument, TableInfo Sales, TableInfo Regions)
    {
        public TableAccess Access() => new(new HashSet<Guid> { SalesDocument, RegionsDocument });
    }

    private static TableJoin JoinRegions(Setup setup) => new(setup.Regions.Id, "r", "Region", "Region");

    /// <summary>
    /// A container with "Sales" (north 10, north 20, south 5, east 40, and a row without a region, 1) and "Regions"
    /// (north alice, south bob, west carol), each from its own document.
    /// </summary>
    private static async Task<Setup> NewAsync(AsyncServiceScope scope)
    {
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var container = await containers.CreateAsync(new CreateContainerRequest($"join-{Guid.NewGuid():N}", null, ContainerKind.Documents));
        Guid containerId = Guid.Parse(container.Id);
        var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
        Guid salesDocument = await NewDocumentAsync(scope, containerId), regionsDocument = await NewDocumentAsync(scope, containerId);
        TableInfo sales = await tables.CreateAsync(containerId, new TableLoad("Sales", SalesColumns,
            Rows([["north", 10m], ["north", 20m], ["south", 5m], ["east", 40m], [null, 1m]]), salesDocument));
        TableInfo regions = await tables.CreateAsync(containerId, new TableLoad("Regions", RegionColumns,
            Rows([["north", "alice"], ["south", "bob"], ["west", "carol"]]), regionsDocument));
        return new Setup(containers, containerId, salesDocument, regionsDocument, sales, regions);
    }

    private static async Task<Guid> NewDocumentAsync(AsyncServiceScope scope, Guid containerId)
    {
        Guid id = Guid.NewGuid();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, ingestion_status, created_at) "
            + "VALUES ({0}, {1}, NULL, {2}, {3}, '', 1, 'Ready', now())",
            id, containerId, $"{id:N}.csv", $"/{id:N}.csv");
        return id;
    }

    /// <summary>Deletes the test's source documents, which a container must not hold when it is deleted, then the container.</summary>
    private static async Task DeleteAsync(AsyncServiceScope scope, Setup setup)
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (KnowledgeDbContext context = await factory.CreateDbContextAsync())
            await context.Database.ExecuteSqlRawAsync("DELETE FROM documents WHERE container_id = {0}", setup.ContainerId);
        await setup.Containers.DeleteAsync(setup.ContainerId);
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Rows(object?[][] rows)
    {
        foreach (object?[] row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }
}

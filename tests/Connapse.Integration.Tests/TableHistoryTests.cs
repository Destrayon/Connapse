using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-306: a table with a key column keeps its history across re-syncs. A re-sync closes the versions that changed or
/// went away and adds the new ones, so agents can read the table as Connapse saw it at a time and ask what changed. A
/// table without a key, or one whose key or columns changed, is replaced and its history starts over.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class TableHistoryTests(SharedWebAppFixture fixture)
{
    private static readonly TableColumn[] Columns =
    [
        new("SKU", TableColumnType.Text),
        new("Name", TableColumnType.Text),
        new("Price", TableColumnType.Number),
    ];

    private static readonly object?[][] FirstRows =
    [
        ["a1", "apple", 1m],
        ["b2", "banana", 2m],
        ["c3", "cherry", 3m],
    ];

    private static readonly object?[][] SecondRows =
    [
        ["a1", "apple", 1m],
        ["b2", "banana", 2.5m],
        ["d4", "date", 4m],
    ];

    [Fact]
    public async Task ReloadAsync_KeyedTable_KeepsStorageAndHistory()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (IContainerStore containers, Guid containerId, TableInfo first) = await NewTableAsync(scope, Columns, FirstRows);
        try
        {
            first.Key.Should().Be("SKU");
            first.HasHistory.Should().BeTrue();

            TableInfo second = await ReloadAsync(scope, containerId, first, Columns, SecondRows);

            second.StorageName.Should().Be(first.StorageName);
            second.Version.Should().Be(first.Version + 1);
            second.Key.Should().Be("SKU");
            second.HistoryFrom.Should().Be(first.HistoryFrom);
            second.RowCount.Should().Be(3);
            second.LoadedAt.Should().BeAfter(first.LoadedAt);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task ReadAsync_SeenAsOfBeforeResync_ReturnsOldRowsAndCurrentReadReturnsNew()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (IContainerStore containers, Guid containerId, TableInfo first) = await NewTableAsync(scope, Columns, FirstRows);
        try
        {
            await ReloadAsync(scope, containerId, first, Columns, SecondRows);
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            TableOrder[] bySku = [new(new TableField("SKU"))];

            TableQueryResult then = await service.ReadAsync(containerId, Access(first),
                new TableReadRequest(first.Id, OrderBy: bySku, SeenAsOf: new DateTimeOffset(first.LoadedAt, TimeSpan.Zero)));
            TableQueryResult now = await service.ReadAsync(containerId, Access(first), new TableReadRequest(first.Id, OrderBy: bySku));

            then.Columns.Should().Equal("SKU", "Name", "Price");
            then.Rows.Should().BeEquivalentTo(FirstRows, options => options.WithStrictOrdering());
            now.Rows.Should().BeEquivalentTo(SecondRows, options => options.WithStrictOrdering());
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task AggregateAsync_SeenAsOf_CountsRowsAsSeenThen()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (IContainerStore containers, Guid containerId, TableInfo first) = await NewTableAsync(scope, Columns, FirstRows);
        try
        {
            await ReloadAsync(scope, containerId, first, Columns, SecondRows);
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            TableField[] sum = [new("Price", TableAggregateFunction.Sum)];

            TableQueryResult then = await service.AggregateAsync(containerId, Access(first),
                new TableAggregateRequest(first.Id, sum, SeenAsOf: new DateTimeOffset(first.LoadedAt, TimeSpan.Zero)));
            TableQueryResult now = await service.AggregateAsync(containerId, Access(first), new TableAggregateRequest(first.Id, sum));

            then.Rows.Single().Single().Should().Be(6m);
            now.Rows.Single().Single().Should().Be(7.5m);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task ChangesAsync_SinceFirstLoad_ListsRemovedAddedAndChangedRows()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (IContainerStore containers, Guid containerId, TableInfo first) = await NewTableAsync(scope, Columns, FirstRows);
        try
        {
            await ReloadAsync(scope, containerId, first, Columns, SecondRows);
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            TableQueryResult changes = await service.ChangesAsync(containerId, Access(first),
                new TableChangesRequest(first.Id, new DateTimeOffset(first.LoadedAt, TimeSpan.Zero)));

            changes.Columns.Should().Equal("change", "version", "SKU", "Name", "Price");
            changes.Rows.Should().BeEquivalentTo(
                new object?[][]
                {
                    ["changed", "old", "b2", "banana", 2m],
                    ["changed", "new", "b2", "banana", 2.5m],
                    ["removed", "old", "c3", "cherry", 3m],
                    ["added", "new", "d4", "date", 4m],
                },
                options => options.WithStrictOrdering());
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task ChangesAsync_UntilFirstLoad_ReturnsNothing()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (IContainerStore containers, Guid containerId, TableInfo first) = await NewTableAsync(scope, Columns, FirstRows);
        try
        {
            await ReloadAsync(scope, containerId, first, Columns, SecondRows);
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            DateTimeOffset loaded = new(first.LoadedAt, TimeSpan.Zero);

            TableQueryResult changes = await service.ChangesAsync(containerId, Access(first),
                new TableChangesRequest(first.Id, loaded, loaded));

            changes.Rows.Should().BeEmpty();
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task ReloadAsync_NoKeyColumn_SwapsStorageAndRefusesSeenAsOf()
    {
        TableColumn[] columns = [new("Fruit", TableColumnType.Text), new("Price", TableColumnType.Number)];
        object?[][] rows = [["apple", 1m], ["apple", 2m]];
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (IContainerStore containers, Guid containerId, TableInfo first) = await NewTableAsync(scope, columns, rows);
        try
        {
            first.Key.Should().BeNull();
            first.HasHistory.Should().BeFalse();

            TableInfo second = await ReloadAsync(scope, containerId, first, columns, rows);
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            Func<Task> act = () => service.ReadAsync(containerId, Access(first),
                new TableReadRequest(first.Id, SeenAsOf: DateTimeOffset.UtcNow));

            second.StorageName.Should().NotBe(first.StorageName);
            second.HasHistory.Should().BeFalse();
            await act.Should().ThrowAsync<TableQueryException>().WithMessage("*has no key column*");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task CreateAsync_KeyNamedColumnWithDuplicates_KeepsNoHistory()
    {
        object?[][] rows = [["a1", "apple", 1m], ["a1", "apricot", 2m]];
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (IContainerStore containers, Guid containerId, TableInfo table) = await NewTableAsync(scope, Columns, rows);
        try
        {
            table.Key.Should().BeNull();
            table.HasHistory.Should().BeFalse();
            TableQueryResult all = await scope.ServiceProvider.GetRequiredService<ITableQueryService>()
                .ReadAsync(containerId, Access(table), new TableReadRequest(table.Id));
            all.Rows.Should().HaveCount(2);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task ReloadAsync_ColumnsChanged_RestartsHistory()
    {
        TableColumn[] wider = [.. Columns, new("Color", TableColumnType.Text)];
        object?[][] widerRows = [["a1", "apple", 1m, "red"]];
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (IContainerStore containers, Guid containerId, TableInfo first) = await NewTableAsync(scope, Columns, FirstRows);
        try
        {
            TableInfo second = await ReloadAsync(scope, containerId, first, wider, widerRows);
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            Func<Task> before = () => service.ReadAsync(containerId, Access(first),
                new TableReadRequest(first.Id, SeenAsOf: new DateTimeOffset(first.LoadedAt, TimeSpan.Zero)));

            second.StorageName.Should().NotBe(first.StorageName);
            second.Key.Should().Be("SKU");
            second.HistoryFrom.Should().BeAfter(first.HistoryFrom!.Value);
            await before.Should().ThrowAsync<TableQueryException>().WithMessage("*can't be read as seen before then*");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task ReloadAsync_KeyNoLongerUnique_RestartsWithoutHistory()
    {
        object?[][] duplicated = [["a1", "apple", 1m], ["a1", "apricot", 2m]];
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (IContainerStore containers, Guid containerId, TableInfo first) = await NewTableAsync(scope, Columns, FirstRows);
        try
        {
            TableInfo second = await ReloadAsync(scope, containerId, first, Columns, duplicated);

            second.StorageName.Should().NotBe(first.StorageName);
            second.HasHistory.Should().BeFalse();
            second.RowCount.Should().Be(2);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    private static TableAccess Access(TableInfo table) => new(new HashSet<Guid> { table.DocumentId!.Value });

    private static async Task<TableInfo> ReloadAsync(
        AsyncServiceScope scope, Guid containerId, TableInfo table, TableColumn[] columns, object?[][] rows)
    {
        TableInfo? reloaded = await scope.ServiceProvider.GetRequiredService<ITableStore>()
            .ReloadAsync(containerId, table.Id, new TableLoad(table.Name, columns, Rows(rows), table.DocumentId));
        return reloaded!;
    }

    private static async Task<(IContainerStore Containers, Guid ContainerId, TableInfo Table)> NewTableAsync(
        AsyncServiceScope scope, TableColumn[] columns, object?[][] rows)
    {
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var container = await containers.CreateAsync(new CreateContainerRequest($"history-{Guid.NewGuid():N}", null, ContainerKind.Documents));
        Guid containerId = Guid.Parse(container.Id);
        TableInfo table = await scope.ServiceProvider.GetRequiredService<ITableStore>()
            .CreateAsync(containerId, new TableLoad("Fruit", columns, Rows(rows), await NewDocumentAsync(scope, containerId)));
        return (containers, containerId, table);
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
    private static async Task DeleteAsync(AsyncServiceScope scope, IContainerStore containers, Guid containerId)
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (KnowledgeDbContext context = await factory.CreateDbContextAsync())
            await context.Database.ExecuteSqlRawAsync("DELETE FROM documents WHERE container_id = {0}", containerId);
        await containers.DeleteAsync(containerId);
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

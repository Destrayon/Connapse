using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Tables;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-301: each tables container keeps its tables in a schema of its own with a catalog entry per table, a reload
/// swaps the new table in without a reader ever seeing part of it, and deleting the container drops its tables.
/// Runs against the services the app registers, so the DI wiring is covered too.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class TableStorageTests(SharedWebAppFixture fixture)
{
    private static readonly TableColumn[] Columns =
    [
        new("Batch", TableColumnType.WholeNumber),
        new("Label", TableColumnType.Text),
        new("Amount", TableColumnType.Number),
        new("Shipped", TableColumnType.Date),
    ];

    [Fact]
    public async Task CreateContainer_Tables_GetsItsOwnSchema_AndOtherKindsDoNot()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();

        var first = await containers.CreateAsync(new CreateContainerRequest(UniqueName("tables-a"), null, ContainerKind.Tables));
        var second = await containers.CreateAsync(new CreateContainerRequest(UniqueName("tables-b"), null, ContainerKind.Tables));
        var documents = await containers.CreateAsync(new CreateContainerRequest(UniqueName("docs"), null, ContainerKind.Documents));
        try
        {
            (await SchemaExistsAsync(scope, Guid.Parse(first.Id))).Should().BeTrue();
            (await SchemaExistsAsync(scope, Guid.Parse(second.Id))).Should().BeTrue();
            TableDdl.SchemaName(Guid.Parse(first.Id)).Should().NotBe(TableDdl.SchemaName(Guid.Parse(second.Id)));
            (await SchemaExistsAsync(scope, Guid.Parse(documents.Id))).Should().BeFalse();
        }
        finally
        {
            await containers.DeleteAsync(Guid.Parse(first.Id));
            await containers.DeleteAsync(Guid.Parse(second.Id));
            await containers.DeleteAsync(Guid.Parse(documents.Id));
        }
    }

    [Fact]
    public async Task Create_TablesContainer_LoadsRowsIntoItsSchemaAndCatalogsThem()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, tables, containerId) = await NewTablesContainerAsync(scope);
        try
        {
            var table = await tables.CreateAsync(containerId, Load("Orders", 3, batch: 1) with { Description = "Q3 orders" });

            table.StorageSchema.Should().Be(TableDdl.SchemaName(containerId));
            table.StorageName.Should().MatchRegex("^t_[0-9a-f]{32}$");
            table.RowCount.Should().Be(3);
            table.Version.Should().Be(1);
            table.Columns.Should().Equal(Columns);
            (await tables.ListAsync(containerId)).Should().ContainSingle().Which.Id.Should().Be(table.Id);

            var rows = await QueryAsync(scope, $"SELECT c_1, c_2, c_3, c_4 FROM \"{table.StorageSchema}\".\"{table.StorageName}\" ORDER BY c_2",
                r => (r.GetInt64(0), r.GetString(1), r.GetDecimal(2), r.IsDBNull(3)));
            rows.Should().Equal((1L, "row-0", 0.5m, false), (1L, "row-1", 1.5m, false), (1L, "row-2", 2.5m, true));
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task Reload_WhileReadersQueryIt_ReadersSeeTheOldTableOrTheNewOneNeverPartOfALoad()
    {
        const int OldRows = 2_000, NewRows = 3_000;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, tables, containerId) = await NewTablesContainerAsync(scope);
        try
        {
            var table = await tables.CreateAsync(containerId, Load("Stock", OldRows, batch: 1));
            using var loading = new CancellationTokenSource();
            bool reloadStarted = false;
            var seen = new ConcurrentBag<(long Count, long MinBatch, long MaxBatch, bool DuringLoad)>();
            var failures = new ConcurrentBag<Exception>();

            // Each reader does what an agent's query will: resolve the table's name from the catalog, then read it.
            async Task ReadUntilAsync(CancellationToken stop)
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
                while (!stop.IsCancellationRequested)
                {
                    // Started and finished while the reload was loading: such a read must still see the old table.
                    bool startedDuringLoad = Volatile.Read(ref reloadStarted) && !loading.IsCancellationRequested;
                    try
                    {
                        await using var context = await factory.CreateDbContextAsync();
                        string name = await context.ContainerTables.AsNoTracking()
                            .Where(t => t.Id == table.Id).Select(t => t.StorageName).SingleAsync();
                        var row = (await QueryAsync(context,
                            $"SELECT count(*), coalesce(min(c_1), 0), coalesce(max(c_1), 0) FROM \"{table.StorageSchema}\".\"{name}\"",
                            r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)))).Single();
                        seen.Add((row.Item1, row.Item2, row.Item3, startedDuringLoad && !loading.IsCancellationRequested));
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                    }
                }
            }

            using var stopReaders = new CancellationTokenSource();
            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => ReadUntilAsync(stopReaders.Token))).ToArray();

            // The new rows trickle in, so the readers run many times while the load is half done.
            Volatile.Write(ref reloadStarted, true);
            var reloaded = await tables.ReloadAsync(containerId, table.Id, Load("Stock", NewRows, batch: 2, pauseEvery: 250));
            loading.Cancel();
            await Task.Delay(200);
            stopReaders.Cancel();
            await Task.WhenAll(readers);

            failures.Should().BeEmpty();
            reloaded!.Version.Should().Be(2);
            reloaded.RowCount.Should().Be(NewRows);
            reloaded.StorageName.Should().NotBe(table.StorageName);
            seen.Should().AllSatisfy(s => s.Should().BeOneOf((OldRows, 1L, 1L, s.DuringLoad), (NewRows, 2L, 2L, s.DuringLoad)));
            seen.Where(s => s.DuringLoad).Should().NotBeEmpty("readers must have run while the reload was loading");
            seen.Should().Contain(s => s.Count == NewRows);
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task Reload_ReaderHoldingTheOldTable_FinishesAndTheOldTableIsDroppedAfterIt()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, tables, containerId) = await NewTablesContainerAsync(scope);
        var store = scope.ServiceProvider.GetRequiredService<PostgresTableStore>();
        try
        {
            var table = await tables.CreateAsync(containerId, Load("Prices", 10, batch: 1));
            string old = $"\"{table.StorageSchema}\".\"{table.StorageName}\"";

            // A reader inside a transaction that resolved the old name before the reload.
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
            await using var reader = await factory.CreateDbContextAsync();
            await using var readerTransaction = await reader.Database.BeginTransactionAsync();
            (await CountAsync(reader, old)).Should().Be(10);

            await tables.ReloadAsync(containerId, table.Id, Load("Prices", 20, batch: 2));

            (await CountAsync(reader, old)).Should().Be(10, "a replaced table stays until its grace period ends");
            await store.DropRetiredTablesAsync(TimeSpan.Zero);
            (await TableExistsAsync(scope, containerId, table.StorageName)).Should().BeTrue("the reader still holds the old table");

            await readerTransaction.CommitAsync();
            await store.DropRetiredTablesAsync(TimeSpan.Zero);
            (await TableExistsAsync(scope, containerId, table.StorageName)).Should().BeFalse();
            (await tables.GetAsync(containerId, table.Id))!.RowCount.Should().Be(20);
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task DeleteContainer_WithTables_DropsItsSchemaTablesAndCatalog()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, tables, containerId) = await NewTablesContainerAsync(scope);
        var first = await tables.CreateAsync(containerId, Load("One", 5, batch: 1));
        await tables.CreateAsync(containerId, Load("Two", 5, batch: 1));
        await tables.ReloadAsync(containerId, first.Id, Load("One", 6, batch: 2));

        (await containers.DeleteAsync(containerId)).Should().BeTrue();

        (await SchemaExistsAsync(scope, containerId)).Should().BeFalse();
        (await tables.ListAsync(containerId)).Should().BeEmpty();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        (await context.RetiredTables.CountAsync(r => r.ContainerId == containerId)).Should().Be(0);
    }

    [Fact]
    public async Task Delete_Table_LeavesTheCatalogAtOnceAndIsDroppedLater()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, tables, containerId) = await NewTablesContainerAsync(scope);
        var store = scope.ServiceProvider.GetRequiredService<PostgresTableStore>();
        try
        {
            var table = await tables.CreateAsync(containerId, Load("Gone", 2, batch: 1));

            (await tables.DeleteAsync(containerId, table.Id)).Should().BeTrue();
            (await tables.GetAsync(containerId, table.Id)).Should().BeNull();
            (await tables.DeleteAsync(containerId, table.Id)).Should().BeFalse();

            await store.DropRetiredTablesAsync(TimeSpan.Zero);
            (await TableExistsAsync(scope, containerId, table.StorageName)).Should().BeFalse();
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task Create_RowOfTheWrongWidth_LeavesNoTableAndNoCatalogEntry()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, tables, containerId) = await NewTablesContainerAsync(scope);
        try
        {
            var load = new TableLoad("Broken", Columns, Rows([[1L, "ok", 1m, null], [1L, "short"]]));

            var create = () => tables.CreateAsync(containerId, load);

            await create.Should().ThrowAsync<ArgumentException>();
            (await tables.ListAsync(containerId)).Should().BeEmpty();
            (await CountTablesInSchemaAsync(scope, containerId)).Should().Be(0);
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task Create_InADocumentsContainer_IsRefused()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
        var documents = await containers.CreateAsync(new CreateContainerRequest(UniqueName("docs"), null, ContainerKind.Documents));
        try
        {
            var create = () => tables.CreateAsync(Guid.Parse(documents.Id), Load("Nope", 1, batch: 1));
            await create.Should().ThrowAsync<InvalidOperationException>();
            (await SchemaExistsAsync(scope, Guid.Parse(documents.Id))).Should().BeFalse();
        }
        finally
        {
            await containers.DeleteAsync(Guid.Parse(documents.Id));
        }
    }

    [Fact]
    public async Task Create_TablesContainerWithoutASchema_MakesTheSchemaOnFirstLoad()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, tables, containerId) = await NewTablesContainerAsync(scope);
        try
        {
            // A tables container made before CON-301 has no schema.
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
            string dropSchema = "DROP SCHEMA " + TableDdl.Quote(TableDdl.SchemaName(containerId));
            await using (var context = await factory.CreateDbContextAsync())
                await context.Database.ExecuteSqlRawAsync(dropSchema);

            var table = await tables.CreateAsync(containerId, Load("Late", 4, batch: 1));

            (await SchemaExistsAsync(scope, containerId)).Should().BeTrue();
            (await TableExistsAsync(scope, containerId, table.StorageName)).Should().BeTrue();
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static async Task<(IContainerStore Containers, ITableStore Tables, Guid ContainerId)> NewTablesContainerAsync(
        AsyncServiceScope scope)
    {
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var container = await containers.CreateAsync(new CreateContainerRequest(UniqueName("tables"), null, ContainerKind.Tables));
        return (containers, scope.ServiceProvider.GetRequiredService<ITableStore>(), Guid.Parse(container.Id));
    }

    /// <summary><paramref name="rows"/> rows, every one carrying <paramref name="batch"/> so a reader can tell loads apart.</summary>
    private static TableLoad Load(string name, int rows, long batch, int pauseEvery = 0) =>
        new(name, Columns, Generate(rows, batch, pauseEvery));

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Generate(
        int rows, long batch, int pauseEvery, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < rows; i++)
        {
            if (pauseEvery > 0 && i > 0 && i % pauseEvery == 0)
                await Task.Delay(50, ct);
            yield return [batch, $"row-{i}", i + 0.5m, i == 2 ? null : new DateOnly(2026, 1, 1).AddDays(i % 300)];
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Rows(object?[][] rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    private static async Task<bool> SchemaExistsAsync(AsyncServiceScope scope, Guid containerId) =>
        await ScalarAsync<bool>(scope,
            $"SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = '{TableDdl.SchemaName(containerId)}')");

    private static async Task<bool> TableExistsAsync(AsyncServiceScope scope, Guid containerId, string tableName) =>
        await ScalarAsync<bool>(scope,
            $"SELECT to_regclass('\"{TableDdl.SchemaName(containerId)}\".\"{tableName}\"') IS NOT NULL");

    private static async Task<long> CountTablesInSchemaAsync(AsyncServiceScope scope, Guid containerId) =>
        await ScalarAsync<long>(scope,
            $"SELECT count(*) FROM pg_tables WHERE schemaname = '{TableDdl.SchemaName(containerId)}'");

    private static async Task<long> CountAsync(KnowledgeDbContext context, string qualifiedName) =>
        (await QueryAsync(context, $"SELECT count(*) FROM {qualifiedName}", r => r.GetInt64(0))).Single();

    private static async Task<T> ScalarAsync<T>(AsyncServiceScope scope, string sql)
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        return (await QueryAsync(context, sql, r => r.GetFieldValue<T>(0))).Single();
    }

    private static async Task<List<T>> QueryAsync<T>(AsyncServiceScope scope, string sql, Func<DbDataReader, T> read)
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        return await QueryAsync(context, sql, read);
    }

    private static async Task<List<T>> QueryAsync<T>(KnowledgeDbContext context, string sql, Func<DbDataReader, T> read)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        await using var reader = await command.ExecuteReaderAsync();
        var results = new List<T>();
        while (await reader.ReadAsync())
            results.Add(read(reader));
        return results;
    }
}

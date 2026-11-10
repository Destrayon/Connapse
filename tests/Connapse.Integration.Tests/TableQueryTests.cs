using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Tables;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-303: agents' table requests run as parameterized SELECTs on a login that can only read tables, and each
/// bypass from the research (docs/research/con-303-safe-query-layer-2026-11-09.md) fails. The bypass tests send raw
/// SQL on the reader login — more than an agent can ever send — and most first switch read-only mode off, to show the
/// grants alone stop them.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class TableQueryTests(SharedWebAppFixture fixture)
{
    private static readonly TableColumn[] Columns =
    [
        new("Region", TableColumnType.Text),
        new("Amount", TableColumnType.Number),
        new("Units", TableColumnType.WholeNumber),
        new("Sold", TableColumnType.Date),
    ];

    private static readonly object?[][] SalesRows =
    [
        ["north", 10.5m, 1L, new DateOnly(2026, 1, 1)],
        ["north", 20m, 2L, new DateOnly(2026, 1, 2)],
        ["south", 5m, 3L, null],
        ["east", 40m, 4L, new DateOnly(2026, 2, 1)],
        [null, 1m, 5L, new DateOnly(2026, 3, 1)],
    ];

    [Fact]
    public async Task ReadAsync_FilterOrderAndLimit_ReturnsMatchingRowsWithTypes()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            TableQueryResult result = await service.ReadAsync(containerId, new TableReadRequest(
                table.Id,
                ["region", "AMOUNT", "Sold"],
                new TableCondition(new TableField("Amount"), TableFilterOperator.GreaterOrEqual, [10]),
                [new TableOrder(new TableField("Amount"), TableSortDirection.Descending)],
                Limit: 2));

            result.Columns.Should().Equal("Region", "Amount", "Sold");
            result.Rows.Should().HaveCount(2);
            result.Rows[0].Should().Equal("east", 40m, new DateOnly(2026, 2, 1));
            result.Rows[1].Should().Equal("north", 20m, new DateOnly(2026, 1, 2));
            result.Truncated.Should().BeTrue("a third row matches");
            result.TruncatedReason.Should().Contain("first 2");
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task AggregateAsync_GroupByWithHaving_ReturnsGroups()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            TableQueryResult result = await service.AggregateAsync(containerId, new TableAggregateRequest(
                table.Id,
                [new TableField(null, TableAggregateFunction.Count), new TableField("Amount", TableAggregateFunction.Sum)],
                ["Region"],
                Where: new TableNotFilter(new TableCondition(new TableField("Region"), TableFilterOperator.IsNull, [])),
                OrderBy: [new TableOrder(new TableField("Amount", TableAggregateFunction.Sum), TableSortDirection.Descending)]));

            result.Columns.Should().Equal("Region", "count", "sum(Amount)");
            result.Rows.Select(r => (r[0], r[1], r[2])).Should().Equal(
                ("east", (object)1L, (object)40m), ("north", 2L, 30.5m), ("south", 1L, 5m));
            result.Truncated.Should().BeFalse();
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task ReadAsync_TableOfAnotherContainer_IsNotFound()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        var (_, otherId, _) = await NewTableAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            Func<Task> act = () => service.ReadAsync(otherId, new TableReadRequest(table.Id));

            await act.Should().ThrowAsync<TableQueryException>().WithMessage("*no table with id*");
        }
        finally
        {
            await containers.DeleteAsync(containerId);
            await containers.DeleteAsync(otherId);
        }
    }

    [Fact]
    public async Task ReadAsync_AfterReload_ReadsTheNewTable()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            (await service.ReadAsync(containerId, new TableReadRequest(table.Id))).Rows.Should().HaveCount(5);

            var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
            await tables.ReloadAsync(containerId, table.Id, new TableLoad("Sales", Columns, Rows([["west", 1m, 1L, null]])));

            TableQueryResult result = await service.ReadAsync(containerId, new TableReadRequest(table.Id, ["Region"]));
            result.Rows.Should().ContainSingle().Which.Should().Equal("west");
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task ReadAsync_HostileHeaderAndValues_AreOnlyData()
    {
        const string Header = "x\"; DROP TABLE container_tables; --";
        const string Value = "'); DROP TABLE container_tables; SELECT ('";
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, _) = await NewTableAsync(scope);
        try
        {
            var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
            TableInfo hostile = await tables.CreateAsync(containerId, new TableLoad(
                "Hostile", [new TableColumn(Header, TableColumnType.Text)], Rows([[Value], ["plain"]])));
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            using JsonDocument request = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["table"] = hostile.Id.ToString(),
                ["where"] = new Dictionary<string, object> { ["column"] = Header, ["op"] = "=", ["value"] = Value },
            }));

            TableQueryResult result = await service.ReadAsync(containerId, TableRequestJson.ParseRead(request.RootElement));

            result.Columns.Should().Equal(Header);
            result.Rows.Should().ContainSingle().Which.Should().Equal(Value);
            (await tables.ListAsync(containerId)).Should().HaveCount(2, "the catalog table is still there");
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task ReadAsync_LongText_IsCutAndMarked()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, _) = await NewTableAsync(scope);
        try
        {
            var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
            TableInfo wide = await tables.CreateAsync(containerId, new TableLoad(
                "Wide", [new TableColumn("Body", TableColumnType.Text)], Rows([[new string('a', 5_000)]])));
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            TableQueryResult result = await service.ReadAsync(containerId, new TableReadRequest(wide.Id));

            ((string)result.Rows[0][0]!).Should().HaveLength(TableQueryCompiler.MaxCellCharacters);
            result.Truncated.Should().BeTrue();
            result.TruncatedReason.Should().Contain("cut");
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task ReaderRole_HasNoRightsBeyondReading()
    {
        var login = fixture.Factory.Services.GetRequiredService<TableReaderLogin>();
        await using NpgsqlConnection connection = await login.OpenConnectionAsync(default);

        await using NpgsqlCommand command = new(
            "SELECT rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls, rolinherit, "
            + "(SELECT count(*) FROM pg_catalog.pg_auth_members m WHERE m.member = r.oid), "
            + "pg_catalog.current_setting('default_transaction_read_only'), pg_catalog.current_setting('statement_timeout'), "
            + "pg_catalog.current_setting('temp_file_limit'), pg_catalog.current_setting('work_mem') "
            + "FROM pg_catalog.pg_roles r WHERE r.rolname = current_user", connection);
        await using NpgsqlDataReader row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();

        Enumerable.Range(0, 6).Select(row.GetBoolean).Should().AllBeEquivalentTo(false);
        row.GetInt64(6).Should().Be(0, "the reader belongs to no role");
        row.GetString(7).Should().Be("on");
        row.GetString(8).Should().Be(TableReaderLogin.StatementTimeout);
        row.GetString(9).Should().Be(TableReaderLogin.TempFileLimit);
        row.GetString(10).Should().Be(TableReaderLogin.WorkMem);
    }

    [Fact]
    public async Task Bypass_WriteInReadOnlyMode_IsRefused()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            PostgresException ex = await ReaderFailsAsync(containerId, table, $"INSERT INTO {Name(table)} (c_1) VALUES ('x')", readOnlyOff: false);

            ex.SqlState.Should().Be(PostgresErrorCodes.ReadOnlySqlTransaction);
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Theory]
    [InlineData("INSERT INTO {table} (c_1) VALUES ('x')")]
    [InlineData("UPDATE {table} SET c_1 = 'x'")]
    [InlineData("DELETE FROM {table}")]
    [InlineData("TRUNCATE {table}")]
    [InlineData("DROP TABLE {table}")]
    [InlineData("ALTER TABLE {table} ADD COLUMN pwned text")]
    [InlineData("CREATE TABLE {schema}.pwned (x int)")]
    [InlineData("CREATE TABLE public.pwned (x int)")]
    [InlineData("SELECT count(*) FROM public.container_tables")]
    [InlineData("SELECT pg_catalog.pg_read_file('/etc/passwd')")]
    [InlineData("SELECT pg_catalog.pg_ls_dir('.')")]
    [InlineData("COPY (SELECT 1) TO PROGRAM 'id'")]
    [InlineData("COPY (SELECT 1) TO '/tmp/pwned'")]
    [InlineData("SELECT pg_catalog.lo_import('/etc/passwd')")]
    [InlineData("CREATE EXTENSION dblink")]
    [InlineData("CREATE SCHEMA pwned")]
    [InlineData("SET ROLE integration_test")]
    [InlineData("SET SESSION AUTHORIZATION integration_test")]
    [InlineData("SET temp_file_limit = -1")]
    public async Task Bypass_WithReadOnlyModeOff_IsRefusedByGrants(string attack)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            string sql = attack.Replace("{table}", Name(table), StringComparison.Ordinal)
                .Replace("{schema}", TableDdl.Quote(table.StorageSchema), StringComparison.Ordinal);

            PostgresException ex = await ReaderFailsAsync(containerId, table, sql, readOnlyOff: true);

            ex.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task Bypass_SecondStatementAfterPositionalParameter_IsASyntaxError()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            // What a compiled query would look like if an agent could append "; COMMIT; INSERT …" to it.
            PostgresException ex = await ReaderFailsAsync(containerId, table,
                $"SELECT c_1 FROM {Name(table)} LIMIT $1; COMMIT; INSERT INTO {Name(table)} (c_1) VALUES ('x')",
                readOnlyOff: false, 10L);

            ex.SqlState.Should().Be(PostgresErrorCodes.SyntaxError);
            ex.MessageText.Should().Contain("multiple commands");
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task Bypass_LongQuery_IsStoppedByStatementTimeout()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            PostgresException ex = await ReaderFailsAsync(containerId, table, "SELECT pg_catalog.pg_sleep(30)", readOnlyOff: false);

            ex.SqlState.Should().Be(PostgresErrorCodes.QueryCanceled);
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    [Fact]
    public async Task Bypass_SessionSettingsChanged_AreResetBeforeTheConnectionIsReused()
    {
        var login = fixture.Factory.Services.GetRequiredService<TableReaderLogin>();
        await using (NpgsqlConnection first = await login.OpenConnectionAsync(default))
        {
            await using NpgsqlCommand off = new("SET default_transaction_read_only = off; SET statement_timeout = 0", first);
            await off.ExecuteNonQueryAsync();
        }

        await using NpgsqlConnection second = await login.OpenConnectionAsync(default);
        await using NpgsqlCommand check = new(
            "SELECT pg_catalog.current_setting('default_transaction_read_only'), pg_catalog.current_setting('statement_timeout')", second);
        await using NpgsqlDataReader row = await check.ExecuteReaderAsync();
        await row.ReadAsync();

        row.GetString(0).Should().Be("on");
        row.GetString(1).Should().Be(TableReaderLogin.StatementTimeout);
    }

    [Fact]
    public async Task Provision_ExistingRoleWithExtraRights_IsRefused()
    {
        var configuration = fixture.Factory.Services.GetRequiredService<IConfiguration>();
        string connectionString = configuration.GetConnectionString("DefaultConnection")!;
        string role = $"con303_overprivileged_{Guid.NewGuid():N}"[..40];
        await using NpgsqlDataSource admin = NpgsqlDataSource.Create(connectionString);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE ROLE {role} LOGIN CREATEDB"))
            await create.ExecuteNonQueryAsync();
        try
        {
            IConfiguration overrides = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Tables:ReaderRole"] = role })
                .Build();
            await using TableReaderLogin login = new(admin, connectionString, overrides, NullLogger<TableReaderLogin>.Instance);

            Func<Task> act = async () => await (await login.OpenConnectionAsync(default)).DisposeAsync();

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*more rights than reading tables*");
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP ROLE {role}");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private async Task<PostgresException> ReaderFailsAsync(Guid containerId, TableInfo table, string sql, bool readOnlyOff, params object[] values)
    {
        var login = fixture.Factory.Services.GetRequiredService<TableReaderLogin>();
        await login.EnsureGrantsAsync(containerId, table.StorageSchema, default);
        await using NpgsqlConnection connection = await login.OpenConnectionAsync(default);
        if (readOnlyOff)
        {
            await using NpgsqlCommand off = new("SET default_transaction_read_only = off", connection);
            await off.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand command = new(sql, connection);
        foreach (object value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        Func<Task> act = () => command.ExecuteNonQueryAsync();
        return (await act.Should().ThrowAsync<PostgresException>()).Which;
    }

    private static string Name(TableInfo table) => $"{TableDdl.Quote(table.StorageSchema)}.{TableDdl.Quote(table.StorageName)}";

    private static async Task<(IContainerStore Containers, Guid ContainerId, TableInfo Table)> NewTableAsync(AsyncServiceScope scope)
    {
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var container = await containers.CreateAsync(new CreateContainerRequest($"query-{Guid.NewGuid():N}", null, ContainerKind.Tables));
        Guid containerId = Guid.Parse(container.Id);
        TableInfo table = await scope.ServiceProvider.GetRequiredService<ITableStore>()
            .CreateAsync(containerId, new TableLoad("Sales", Columns, Rows(SalesRows)));
        return (containers, containerId, table);
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

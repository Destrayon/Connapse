using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Tables;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
/// grants alone stop them. Every table gets a source document, and each request names the documents its caller may
/// read (the CON-320 plan's readable set); a table outside that set reads as missing.
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

            TableQueryResult result = await service.ReadAsync(containerId, Access(table), new TableReadRequest(
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
            result.Notice.Should().Be(TableQueryResult.UntrustedDataNotice);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
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

            TableQueryResult result = await service.AggregateAsync(containerId, Access(table), new TableAggregateRequest(
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
            await DeleteAsync(scope, containers, containerId);
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

            Func<Task> act = () => service.ReadAsync(otherId, Access(table), new TableReadRequest(table.Id));

            await act.Should().ThrowAsync<TableQueryException>().WithMessage("*no table with id*");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
            await DeleteAsync(scope, containers, otherId);
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
            (await service.ReadAsync(containerId, Access(table), new TableReadRequest(table.Id))).Rows.Should().HaveCount(5);

            var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
            await tables.ReloadAsync(containerId, table.Id, new TableLoad("Sales", Columns, Rows([["west", 1m, 1L, null]]), table.DocumentId));

            TableQueryResult result = await service.ReadAsync(containerId, Access(table), new TableReadRequest(table.Id, ["Region"]));
            result.Rows.Should().ContainSingle().Which.Should().Equal("west");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
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
                "Hostile", [new TableColumn(Header, TableColumnType.Text)], Rows([[Value], ["plain"]]), await NewDocumentAsync(scope, containerId)));
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            using JsonDocument request = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["table"] = hostile.Id.ToString(),
                ["where"] = new Dictionary<string, object> { ["column"] = Header, ["op"] = "=", ["value"] = Value },
            }));

            TableQueryResult result = await service.ReadAsync(containerId, Access(hostile), TableRequestJson.ParseRead(request.RootElement));

            result.Columns.Should().Equal(Header);
            result.Rows.Should().ContainSingle().Which.Should().Equal(Value);
            (await tables.ListAsync(containerId)).Should().HaveCount(2, "the catalog table is still there");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
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
                "Wide", [new TableColumn("Body", TableColumnType.Text)], Rows([[new string('a', 5_000)]]), await NewDocumentAsync(scope, containerId)));
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            TableQueryResult result = await service.ReadAsync(containerId, Access(wide), new TableReadRequest(wide.Id));

            ((string)result.Rows[0][0]!).Should().HaveLength(TableQueryCompiler.MaxCellCharacters);
            result.Truncated.Should().BeTrue();
            result.TruncatedReason.Should().Contain("cut");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task ReaderRole_HasNoRightsBeyondReading()
    {
        var login = fixture.Factory.Services.GetRequiredService<TableReaderLogin>();
        await using NpgsqlConnection connection = await login.OpenConnectionAsync(default);

        await using NpgsqlCommand command = new(
            "SELECT rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls, rolinherit, "
            + "(SELECT count(*) FROM pg_catalog.pg_auth_members m JOIN pg_catalog.pg_roles g ON g.oid = m.roleid WHERE m.member = r.oid "
            + "AND NOT (m.set_option AND NOT m.inherit_option AND NOT m.admin_option AND g.rolname LIKE 'connapse\\_tr\\_%')), "
            + "pg_catalog.current_setting('default_transaction_read_only'), pg_catalog.current_setting('statement_timeout'), "
            + "pg_catalog.current_setting('temp_file_limit'), pg_catalog.current_setting('work_mem'), "
            + "pg_catalog.current_setting('max_parallel_workers_per_gather') "
            + "FROM pg_catalog.pg_roles r WHERE r.rolname = current_user", connection);
        await using NpgsqlDataReader row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();

        Enumerable.Range(0, 6).Select(row.GetBoolean).Should().AllBeEquivalentTo(false);
        row.GetInt64(6).Should().Be(0, "the reader belongs to no role but container readers it can only SET ROLE to");
        row.GetString(7).Should().Be("on");
        row.GetString(8).Should().Be(TableReaderLogin.StatementTimeout);
        row.GetString(9).Should().Be(TableReaderLogin.TempFileLimit);
        row.GetString(10).Should().Be(TableReaderLogin.WorkMem);
        row.GetString(11).Should().Be(TableQueryRunner.MaxParallelWorkersPerGather);
    }

    [Fact]
    public async Task ReadAndAggregate_SourceDocumentNotReadable_AreNotFoundWithoutNamingColumns()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            TableAccess other = new(new HashSet<Guid> { Guid.NewGuid() });

            Func<Task> read = () => service.ReadAsync(containerId, other, new TableReadRequest(table.Id, ["no such column"]));
            Func<Task> aggregate = () => service.AggregateAsync(containerId, other,
                new TableAggregateRequest(table.Id, [new TableField(null, TableAggregateFunction.Count)]));

            (await read.Should().ThrowAsync<TableQueryException>().WithMessage("*no table with id*"))
                .Which.Message.Should().NotContain("Region", "an unreadable table's columns are never named");
            await aggregate.Should().ThrowAsync<TableQueryException>().WithMessage("*no table with id*");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task ReadAsync_TableWithNoSourceDocument_IsNotFound()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            TableInfo orphan = await scope.ServiceProvider.GetRequiredService<ITableStore>()
                .CreateAsync(containerId, new TableLoad("Orphan", Columns, Rows(SalesRows)));
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            Func<Task> act = () => service.ReadAsync(containerId, Access(table), new TableReadRequest(orphan.Id));

            await act.Should().ThrowAsync<TableQueryException>().WithMessage("*no table with id*");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task ReadAsync_AnswerOverTheByteCap_IsCutAndMarked()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, _) = await NewTableAsync(scope);
        try
        {
            // Two UTF-8 bytes per character: 40 rows of 1,999 characters are about 160 KB, over the byte cap while
            // every cell is under the character cap.
            object?[][] rows = Enumerable.Range(0, 40).Select(_ => new object?[] { new string('é', 1_999) }).ToArray();
            TableInfo accents = await scope.ServiceProvider.GetRequiredService<ITableStore>().CreateAsync(containerId, new TableLoad(
                "Accents", [new TableColumn("Body", TableColumnType.Text)], Rows(rows), await NewDocumentAsync(scope, containerId)));
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            TableQueryResult result = await service.ReadAsync(containerId, Access(accents), new TableReadRequest(accents.Id, Limit: 100));

            result.Rows.Should().HaveCount((TableQueryRunner.MaxAnswerBytes / (1_999 * 2)) + 1);
            result.Truncated.Should().BeTrue();
            result.TruncatedReason.Should().Contain("bytes");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Runner_ClientDeadline_CancelsTheQueryOnTheServer()
    {
        const string Sleep = "SELECT pg_catalog.pg_sleep(3)";
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewGrantedTableAsync(scope);
        try
        {
            TableQueryRunner runner = new(fixture.Factory.Services.GetRequiredService<TableReaderLogin>())
            {
                ClientTimeout = TimeSpan.FromMilliseconds(500),
            };
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

            Func<Task> act = () => runner.RunAsync(containerId, Access(table), new CompiledTableQuery(Sleep, [], null, 1), default);

            await act.Should().ThrowAsync<TableQueryException>().WithMessage("*took longer than*");
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.5), "the client gave up before the server's timeout");
            await using NpgsqlDataSource admin = AdminDataSource();
            await using NpgsqlCommand running = admin.CreateCommand(
                "SELECT count(*) FROM pg_catalog.pg_stat_activity WHERE state = 'active' AND query = $1");
            running.Parameters.Add(new NpgsqlParameter { Value = Sleep });
            ((long)(await running.ExecuteScalarAsync())!).Should().Be(0, "the server stopped the query, not just the client");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Runner_QueryWithoutColumns_TakesTheDatabaseNamesAndIsNeverSplit()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewGrantedTableAsync(scope);
        try
        {
            TableQueryRunner runner = new(fixture.Factory.Services.GetRequiredService<TableReaderLogin>());

            TableQueryResult result = await runner.RunAsync(containerId, Access(table), new CompiledTableQuery("SELECT 1 AS one", [], null, 10), default);
            Func<Task> stacked = () => runner.RunAsync(containerId, Access(table),
                new CompiledTableQuery("SELECT 1 AS one; SET default_transaction_read_only = off", [], null, 10), default);

            result.Columns.Should().Equal("one");
            result.Rows.Should().ContainSingle().Which.Should().Equal(1);
            result.Notice.Should().Be(TableQueryResult.UntrustedDataNotice);
            (await stacked.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.SyntaxError);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Rls_RightToken_SeesEveryRow()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewGrantedTableAsync(scope);
        try
        {
            string token = await Login.IssueTokenAsync(Access(table).ReadableDocuments, TimeSpan.FromMinutes(1), default);

            (await CountUnderRoleAsync(containerId, table, token)).Should().Be(SalesRows.Length);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Rls_NoToken_SeesNoRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewGrantedTableAsync(scope);
        try
        {
            (await CountUnderRoleAsync(containerId, table, token: null)).Should().Be(0);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Rls_ForgedToken_SeesNoRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewGrantedTableAsync(scope);
        try
        {
            await Login.IssueTokenAsync(Access(table).ReadableDocuments, TimeSpan.FromMinutes(1), default);
            string forged = System.Security.Cryptography.RandomNumberGenerator.GetHexString(32, lowercase: true);

            (await CountUnderRoleAsync(containerId, table, forged)).Should().Be(0, "a token nobody issued lists no documents");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Rls_AnotherCallersToken_SeesNoRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewGrantedTableAsync(scope);
        try
        {
            string other = await Login.IssueTokenAsync(new HashSet<Guid> { Guid.NewGuid() }, TimeSpan.FromMinutes(1), default);

            (await CountUnderRoleAsync(containerId, table, other)).Should().Be(0);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Rls_ExpiredToken_SeesNoRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewGrantedTableAsync(scope);
        try
        {
            string expired = await Login.IssueTokenAsync(Access(table).ReadableDocuments, TimeSpan.FromSeconds(-1), default);

            (await CountUnderRoleAsync(containerId, table, expired)).Should().Be(0);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Rls_PlantedAppSideLeak_DatabaseStillReturnsNoRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewGrantedTableAsync(scope);
        try
        {
            // The app-side check is skipped: the runner gets a query for the table with a caller who can't read it.
            var runner = fixture.Factory.Services.GetRequiredService<TableQueryRunner>();
            TableAccess stranger = new(new HashSet<Guid> { Guid.NewGuid() });
            CompiledTableQuery read = TableQueryCompiler.CompileRead(table, new TableReadRequest(table.Id));
            CompiledTableQuery count = TableQueryCompiler.CompileAggregate(table,
                new TableAggregateRequest(table.Id, [new TableField(null, TableAggregateFunction.Count)]));

            (await runner.RunAsync(containerId, stranger, read, default)).Rows.Should().BeEmpty();
            (await runner.RunAsync(containerId, stranger, count, default)).Rows.Should().ContainSingle().Which.Should().Equal(0L);
            (await runner.RunAsync(containerId, Access(table), read, default)).Rows.Should().HaveCount(SalesRows.Length, "the same query works for a reader");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Rls_TableOfAnotherContainerInTheSameQuery_SeesNoRowsWithoutItsDocument()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewGrantedTableAsync(scope);
        var (_, otherId, otherTable) = await NewGrantedTableAsync(scope);
        try
        {
            var runner = fixture.Factory.Services.GetRequiredService<TableQueryRunner>();

            Func<Task> act = () => runner.RunAsync(containerId, Access(table),
                new CompiledTableQuery($"SELECT count(*) FROM {Name(otherTable)}", [], null, 10), default);

            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "a container's role has no grants on another container's schema");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
            await DeleteAsync(scope, containers, otherId);
        }
    }

    [Fact]
    public async Task Rls_LoginWithoutContainerRole_IsRefused()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewGrantedTableAsync(scope);
        try
        {
            string token = await Login.IssueTokenAsync(Access(table).ReadableDocuments, TimeSpan.FromMinutes(1), default);
            await using NpgsqlConnection connection = await Login.OpenConnectionAsync(default);
            await using (NpgsqlCommand bind = new("SELECT pg_catalog.set_config('tables_auth.token', $1, false)", connection))
            {
                bind.Parameters.Add(new NpgsqlParameter { Value = token });
                await bind.ExecuteNonQueryAsync();
            }
            await using NpgsqlCommand command = new($"RESET ROLE; SELECT count(*) FROM {Name(table)}", connection);

            Func<Task> act = () => command.ExecuteScalarAsync();

            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Theory]
    [InlineData("SELECT count(*) FROM tables_auth.requests")]
    [InlineData("INSERT INTO tables_auth.requests VALUES ('forged', ARRAY[]::uuid[], now() + interval '1 hour')")]
    [InlineData("UPDATE tables_auth.requests SET expires_at = now() + interval '1 hour'")]
    [InlineData("CREATE OR REPLACE FUNCTION tables_auth.readable_documents() RETURNS uuid[] LANGUAGE sql AS 'SELECT NULL::uuid[]'")]
    [InlineData("CREATE TEMP TABLE pwned (x int)")]
    [InlineData("ALTER TABLE {table} DISABLE ROW LEVEL SECURITY")]
    [InlineData("ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY")]
    [InlineData("CREATE POLICY pwned ON {table} FOR SELECT USING (true)")]
    [InlineData("DROP POLICY readable ON {table}")]
    public async Task Rls_ContainerRoleCannotForgeItsIdentity(string attack)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewGrantedTableAsync(scope);
        try
        {
            string sql = attack.Replace("{table}", Name(table), StringComparison.Ordinal);

            PostgresException ex = await ReaderFailsAsync(containerId, table, sql, readOnlyOff: true);

            ex.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Rls_QueryThroughTheService_DeletesItsToken()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            await using NpgsqlDataSource admin = AdminDataSource();
            await using NpgsqlCommand before = admin.CreateCommand(
                "SELECT coalesce(array_agg(token), '{}') FROM tables_auth.requests");
            string[] earlier = (string[])(await before.ExecuteScalarAsync())!;

            await service.ReadAsync(containerId, Access(table), new TableReadRequest(table.Id));

            await using NpgsqlCommand live = admin.CreateCommand(
                "SELECT count(*) FROM tables_auth.requests WHERE NOT (token = ANY ($1))");
            live.Parameters.Add(new NpgsqlParameter { Value = earlier });
            ((long)(await live.ExecuteScalarAsync())!).Should().Be(0, "the token is deleted once the query is done");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Rls_TableBuiltBeforeAnyQuery_CreatesTheTokenStoreItself()
    {
        // Ingestion builds tables before any query provisions the reader, so a fresh database has no token store yet.
        string database = "con324_fresh_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = AdminDataSource();
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {database}"))
            await create.ExecuteNonQueryAsync();
        try
        {
            string main = fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!;
            NpgsqlConnectionStringBuilder fresh = new(main) { Database = database };
            await using NpgsqlConnection connection = new(fresh.ConnectionString);
            await connection.OpenAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            Guid containerId = Guid.NewGuid();
            await TableDdl.CreateSchemaAsync(connection, containerId, CancellationToken.None);
            await TableDdl.ExecuteAsync(connection,
                TableDdl.CreateTableSql(containerId, "t_1", [new TableColumn("a", TableColumnType.Text)]), CancellationToken.None);

            await TableDdl.EnsureTokenStoreAsync(connection, CancellationToken.None);
            foreach (string sql in TableDdl.ProtectSql(containerId, "t_1", Guid.NewGuid()))
                await TableDdl.ExecuteAsync(connection, sql, CancellationToken.None);
            await TableDdl.EnsureTokenStoreAsync(connection, CancellationToken.None);
            await transaction.CommitAsync();

            await using NpgsqlCommand policies = new("SELECT count(*) FROM pg_catalog.pg_policies WHERE policyname = 'readable'", connection);
            ((long)(await policies.ExecuteScalarAsync())!).Should().Be(1);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Rls_LoadedTable_ForcesRowSecurityOnItsOwnerToo()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            await using NpgsqlDataSource admin = AdminDataSource();
            await using NpgsqlCommand flags = admin.CreateCommand(
                "SELECT c.relrowsecurity, c.relforcerowsecurity, (SELECT count(*) FROM pg_catalog.pg_policy p WHERE p.polrelid = c.oid) "
                + "FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = $1 AND c.relname = $2");
            flags.Parameters.Add(new NpgsqlParameter { Value = table.StorageSchema });
            flags.Parameters.Add(new NpgsqlParameter { Value = table.StorageName });
            await using NpgsqlDataReader row = await flags.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();

            row.GetBoolean(0).Should().BeTrue("row-level security is on");
            row.GetBoolean(1).Should().BeTrue("it holds the owning main login too (the test login is a superuser, which skips it)");
            row.GetInt64(2).Should().Be(1, "one policy carries the document check");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Rls_ContainerDelete_DropsItsRole()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, _) = await NewGrantedTableAsync(scope);
        string role = Login.ContainerRoleName(containerId);
        await using NpgsqlDataSource admin = AdminDataSource();
        await using NpgsqlCommand exists = admin.CreateCommand("SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname = $1");
        exists.Parameters.Add(new NpgsqlParameter { Value = role });
        ((long)(await exists.ExecuteScalarAsync())!).Should().Be(1);

        await DeleteAsync(scope, containers, containerId);

        ((long)(await exists.ExecuteScalarAsync())!).Should().Be(0);
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
            await DeleteAsync(scope, containers, containerId);
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
            await DeleteAsync(scope, containers, containerId);
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
            await DeleteAsync(scope, containers, containerId);
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
            await DeleteAsync(scope, containers, containerId);
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
        // Queries run as the container's reader role (CON-324), so the attacks do too.
        await using (NpgsqlCommand role = new($"SET ROLE {TableDdl.Quote(login.ContainerRoleName(containerId))}", connection))
            await role.ExecuteNonQueryAsync();
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

    private TableReaderLogin Login => fixture.Factory.Services.GetRequiredService<TableReaderLogin>();

    private NpgsqlDataSource AdminDataSource() => NpgsqlDataSource.Create(
        fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!);

    /// <summary>Counts a table's rows the way a query sees them: on the reader login, as the container's role, with a token or none.</summary>
    private async Task<long> CountUnderRoleAsync(Guid containerId, TableInfo table, string? token)
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
        await using NpgsqlCommand count = new($"SELECT count(*) FROM {Name(table)}", connection, transaction);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private async Task<(IContainerStore Containers, Guid ContainerId, TableInfo Table)> NewGrantedTableAsync(AsyncServiceScope scope)
    {
        var created = await NewTableAsync(scope);
        await Login.EnsureGrantsAsync(created.ContainerId, created.Table.StorageSchema, default);
        return created;
    }

    private static TableAccess Access(TableInfo table) => new(new HashSet<Guid> { table.DocumentId!.Value });

    private static string Name(TableInfo table) => $"{TableDdl.Quote(table.StorageSchema)}.{TableDdl.Quote(table.StorageName)}";

    private static async Task<(IContainerStore Containers, Guid ContainerId, TableInfo Table)> NewTableAsync(AsyncServiceScope scope)
    {
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var container = await containers.CreateAsync(new CreateContainerRequest($"query-{Guid.NewGuid():N}", null, ContainerKind.Documents));
        Guid containerId = Guid.Parse(container.Id);
        TableInfo table = await scope.ServiceProvider.GetRequiredService<ITableStore>()
            .CreateAsync(containerId, new TableLoad("Sales", Columns, Rows(SalesRows), await NewDocumentAsync(scope, containerId)));
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

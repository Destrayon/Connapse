using System.Net;
using System.Net.Http.Json;
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
/// CON-326: agents send their own read-only SELECT over a container's tables. Every bypass in the research
/// (docs/research/rls-raw-sql-agents-2026-11-10.md, "Bypasses and what blocks each") fails through the service; the
/// rows a database layer blocks fail again with the parser switched off; and planted leaks against a table the
/// caller can't read return no rows. The REST route and MCP tool refuse until an administrator turns the setting on.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class TableSqlTests(SharedWebAppFixture fixture)
{
    private static readonly TableColumn[] Columns =
    [
        new("Region", TableColumnType.Text),
        new("Amount", TableColumnType.Number),
        new("Units", TableColumnType.WholeNumber),
    ];

    private static readonly object?[][] SalesRows =
    [
        ["north", 10.5m, 1L],
        ["north", 20m, 2L],
        ["south", 5m, 3L],
        ["east", 40m, 4L],
        [null, 1m, 5L],
    ];

    private static readonly TableColumn[] SecretColumns =
    [
        new("Salary", TableColumnType.Text),
        new("Bonus", TableColumnType.WholeNumber),
    ];

    private static readonly object?[][] SecretRows = [["not a number", 1L], ["hidden", 0L]];

    [Fact]
    public async Task RunAsync_SelectByGeneratedNames_ReturnsRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableSqlService>();

            TableQueryResult grouped = await service.RunAsync(containerId, Access(table),
                $"SELECT c_1 AS region, sum(c_2) AS total FROM {table.StorageName} WHERE c_1 IS NOT NULL GROUP BY c_1 ORDER BY total DESC");
            TableQueryResult ranked = await service.RunAsync(containerId, Access(table),
                $"WITH s AS (SELECT c_1, c_3 FROM {table.StorageName}) SELECT c_1, rank() OVER (ORDER BY c_3 DESC) AS r FROM s ORDER BY r LIMIT 2");

            grouped.Columns.Should().Equal("region", "total");
            grouped.Rows.Select(r => (r[0], r[1])).Should().Equal(("east", (object)40m), ("north", 30.5m), ("south", 5m));
            grouped.Notice.Should().Be(TableQueryResult.UntrustedDataNotice);
            ranked.Rows.Select(r => r[0]).Should().Equal(new object?[] { null, "east" });
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Theory]
    [InlineData("SELECT 1; COMMIT; INSERT INTO {table} (c_1) VALUES ('x')")]
    [InlineData("COMMIT; INSERT INTO {table} (c_1) VALUES ('x')")]
    [InlineData("INSERT INTO {table} (c_1) VALUES ('x')")]
    [InlineData("DELETE FROM {table}")]
    [InlineData("SET default_transaction_read_only = off")]
    [InlineData("SET TRANSACTION READ WRITE")]
    [InlineData("SELECT pg_catalog.set_config('tables_auth.token', 'forged', true)")]
    [InlineData("SELECT set_config('tables_auth.token', 'forged', true), count(*) FROM {table}")]
    [InlineData("SELECT current_setting('tables_auth.token')")]
    [InlineData("SET tables_auth.token = 'forged'")]
    [InlineData("SET ROLE integration_test")]
    [InlineData("RESET ROLE")]
    [InlineData("SET SESSION AUTHORIZATION integration_test")]
    [InlineData("DO $$BEGIN RAISE NOTICE 'x'; END$$")]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE sql AS 'SELECT 1'")]
    [InlineData("SELECT * FROM pg_stats")]
    [InlineData("SELECT * FROM pg_catalog.pg_stats_ext")]
    [InlineData("EXPLAIN SELECT * FROM {table}")]
    [InlineData("SELECT reltuples FROM pg_class")]
    [InlineData("SELECT n_live_tup FROM pg_stat_user_tables")]
    [InlineData("SELECT pg_relation_size('{table}')")]
    [InlineData("SELECT attname FROM pg_attribute")]
    [InlineData("SELECT '{table}'::regclass")]
    [InlineData("SELECT query FROM pg_stat_activity")]
    [InlineData("SELECT pg_cancel_backend(1)")]
    [InlineData("SELECT pg_terminate_backend(1)")]
    [InlineData("SELECT pg_advisory_lock(1)")]
    [InlineData("NOTIFY pwned")]
    [InlineData("SELECT pg_notify('pwned', 'x')")]
    [InlineData("LISTEN pwned")]
    [InlineData("DECLARE c CURSOR WITH HOLD FOR SELECT 1")]
    [InlineData("LOCK TABLE {table}")]
    [InlineData("SELECT lo_create(0)")]
    [InlineData("SELECT lo_import('/etc/passwd')")]
    [InlineData("SELECT pg_read_file('/etc/passwd')")]
    [InlineData("SELECT \"pg_catalog\".\"pg_read_file\"('/etc/passwd')")]
    [InlineData("SELECT PG_CATALOG.PG_READ_FILE('/etc/passwd')")]
    [InlineData("COPY (SELECT 1) TO PROGRAM 'id'")]
    [InlineData("SELECT * FROM dblink('host=localhost', 'SELECT 1') AS t(a int)")]
    [InlineData("SELECT pg_sleep(30)")]
    [InlineData("SELECT * FROM generate_series(1, 1000000000)")]
    [InlineData("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r) SELECT n FROM r")]
    [InlineData("SELECT count(*) FROM public.container_tables")]
    [InlineData("SELECT token FROM tables_auth.requests")]
    [InlineData("SELECT * FROM {table}, {table} a, {table} b, {table} c")]
    public async Task RunAsync_ResearchBypass_IsRefusedAndChangesNothing(string attack)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableSqlService>();
            string sql = attack.Replace("{table}", table.StorageName, StringComparison.Ordinal);

            Func<Task> act = () => service.RunAsync(containerId, Access(table), sql);

            if (attack.StartsWith("SELECT * FROM {table}, {table}", StringComparison.Ordinal))
            {
                // A cross join of 5^4 rows is a SELECT the parser allows; the row cap is what holds it.
                (await service.RunAsync(containerId, Access(table), sql)).Rows.Count.Should().BeLessThanOrEqualTo(TableSqlService.MaxRows);
            }
            else
            {
                (await act.Should().ThrowAsync<TableQueryException>()).Which.Message.Should().NotContain("forged");
            }
            (await AdminCountAsync(table)).Should().Be(SalesRows.Length, "nothing was written");
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
    [InlineData("SELECT 1; INSERT INTO {table} (c_1) VALUES ('x')")]
    [InlineData("COMMIT; INSERT INTO {table} (c_1) VALUES ('x')")]
    [InlineData("CREATE TEMP TABLE pwned (x int)")]
    [InlineData("CREATE TABLE {schema}.pwned (x int)")]
    [InlineData("CREATE FUNCTION {schema}.f() RETURNS int LANGUAGE sql AS 'SELECT 1'")]
    [InlineData("SET ROLE integration_test")]
    [InlineData("SET SESSION AUTHORIZATION integration_test")]
    [InlineData("SELECT token FROM tables_auth.requests")]
    [InlineData("SELECT count(*) FROM public.container_tables")]
    [InlineData("SELECT pg_catalog.pg_read_file('/etc/passwd')")]
    [InlineData("SELECT pg_catalog.pg_ls_dir('.')")]
    [InlineData("COPY (SELECT 1) TO PROGRAM 'id'")]
    [InlineData("SELECT pg_catalog.lo_create(0)")]
    [InlineData("SELECT * FROM dblink('host=localhost', 'SELECT 1') AS t(a int)")]
    public async Task RunAsync_ParserOff_DatabaseLayersStillRefuse(string attack)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            TableSqlService service = Unchecked(scope);
            string sql = attack.Replace("{table}", table.StorageName, StringComparison.Ordinal)
                .Replace("{schema}", TableDdl.Quote(table.StorageSchema), StringComparison.Ordinal);

            Func<Task> act = () => service.RunAsync(containerId, Access(table), sql);

            await act.Should().ThrowAsync<TableQueryException>();
            (await AdminCountAsync(table)).Should().Be(SalesRows.Length, "nothing was written");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task RunAsync_ParserOff_LongQueryIsStoppedByTheTimeout()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            Func<Task> act = () => Unchecked(scope).RunAsync(containerId, Access(table), "SELECT pg_catalog.pg_sleep(30)");

            await act.Should().ThrowAsync<TableQueryException>().WithMessage("*longer than*");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Theory]
    [InlineData("SELECT * FROM {secret}")]
    [InlineData("SELECT count(*) FROM {secret}")]
    [InlineData("SELECT c_1 FROM {table} UNION ALL SELECT c_1 FROM {secret}")]
    [InlineData("SELECT s.c_1 FROM {table} t JOIN {secret} s ON true")]
    [InlineData("SELECT 1 FROM {secret} WHERE c_1::int = 1")]
    [InlineData("SELECT 1 / c_2 FROM {secret}")]
    [InlineData("SELECT (SELECT pg_catalog.set_config('tables_auth.token', 'forged', true)), c_1 FROM {secret}")]
    [InlineData("SELECT c_1 FROM {secret} WHERE pg_catalog.set_config('tables_auth.token', 'forged', true) IS NOT NULL")]
    public async Task RunAsync_PlantedLeakOnUnreadableTable_ReturnsNoHiddenRows(string attack)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            TableInfo secret = await scope.ServiceProvider.GetRequiredService<ITableStore>().CreateAsync(containerId,
                new TableLoad("Secret", SecretColumns, Rows(SecretRows), await NewDocumentAsync(scope, containerId)));
            // The parser is off, so nothing app-side keeps the query to the readable table: only the database does.
            string sql = attack.Replace("{table}", table.StorageName, StringComparison.Ordinal)
                .Replace("{secret}", secret.StorageName, StringComparison.Ordinal);

            TableQueryResult result = await Unchecked(scope).RunAsync(containerId, Access(table), sql);

            if (attack.StartsWith("SELECT count(*)", StringComparison.Ordinal))
                result.Rows.Should().ContainSingle().Which.Should().Equal(0L);
            else if (attack.Contains("UNION ALL", StringComparison.Ordinal))
                result.Rows.Should().HaveCount(SalesRows.Length, "only the readable table's rows come back");
            else
                result.Rows.Should().BeEmpty();
            JsonSerializer.Serialize(result.Rows).Should().NotContain("hidden").And.NotContain("not a number");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task RunAsync_ParserOn_UnreadableTableIsRefusedWithoutNamingIt()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            TableInfo secret = await scope.ServiceProvider.GetRequiredService<ITableStore>().CreateAsync(containerId,
                new TableLoad("Secret", SecretColumns, Rows(SecretRows), await NewDocumentAsync(scope, containerId)));
            var service = scope.ServiceProvider.GetRequiredService<ITableSqlService>();

            Func<Task> act = () => service.RunAsync(containerId, Access(table), $"SELECT * FROM {secret.StorageName}");

            string message = (await act.Should().ThrowAsync<TableQueryException>()).Which.Message;
            message.Should().Contain(table.StorageName).And.Contain("Region");
            message.Should().NotContain("Secret").And.NotContain("Salary").And.NotContain("Bonus");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task RunAsync_ParserOff_TableOfAnotherContainerIsRefused()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        var (_, otherId, other) = await NewTableAsync(scope);
        try
        {
            await scope.ServiceProvider.GetRequiredService<TableReaderLogin>().EnsureGrantsAsync(otherId, other.StorageSchema, default);
            string sql = $"SELECT count(*) FROM {TableDdl.Quote(other.StorageSchema)}.{TableDdl.Quote(other.StorageName)}";

            Func<Task> act = () => Unchecked(scope).RunAsync(containerId, Access(table, other), sql);

            await act.Should().ThrowAsync<TableQueryException>().WithMessage("*may not read*");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
            await DeleteAsync(scope, containers, otherId);
        }
    }

    [Fact]
    public async Task RunAsync_ErrorFromAReadableRow_ReturnsOnlyTheMessage()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableSqlService>();

            Func<Task> act = () => service.RunAsync(containerId, Access(table), $"SELECT c_3 / (c_3 - 1) FROM {table.StorageName}");

            string message = (await act.Should().ThrowAsync<TableQueryException>()).Which.Message;
            message.Should().Contain("couldn't be computed").And.Contain("division by zero");
            message.Should().NotContain("tables_auth").And.NotContain("PL/pgSQL").And.NotContain("connapse_tr_");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Surfaces_SettingOffByDefault_RefuseUntilAnAdministratorTurnsItOn()
    {
        var http = fixture.AdminClient;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            string sql = $"SELECT c_1, c_3 FROM {table.StorageName} WHERE c_3 <= 2 ORDER BY c_3";
            string route = $"/api/containers/{containerId}/tables/sql";

            using (var off = await http.PostAsJsonAsync(route, new { sql }))
            {
                off.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await off.Content.ReadAsStringAsync()).Should().Contain("Tables.AgentSql");
            }
            await using (var mcp = await CrossSurface.Surfaces.McpAsync(fixture))
                Text(await mcp.CallToolAsync("table_sql", new Dictionary<string, object?> { ["containerId"] = containerId.ToString(), ["sql"] = sql }))
                    .Should().Contain("Tables.AgentSql");

            using (var on = await http.PutAsJsonAsync($"/api/containers/{containerId}/settings", new { tables = new { agentSql = true } }))
                on.StatusCode.Should().Be(HttpStatusCode.OK, await on.Content.ReadAsStringAsync());

            using (var ran = await http.PostAsJsonAsync(route, new { sql }))
            {
                ran.StatusCode.Should().Be(HttpStatusCode.OK, await ran.Content.ReadAsStringAsync());
                using JsonDocument body = JsonDocument.Parse(await ran.Content.ReadAsStringAsync());
                body.RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).Should().Equal("c_1", "c_3");
                body.RootElement.GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()).Should().Equal("north", "north");
            }
            await using (var mcp = await CrossSurface.Surfaces.McpAsync(fixture))
            {
                string text = Text(await mcp.CallToolAsync("table_sql", new Dictionary<string, object?> { ["containerId"] = containerId.ToString(), ["sql"] = sql }));
                using JsonDocument body = JsonDocument.Parse(text);
                body.RootElement.GetProperty("rows").GetArrayLength().Should().Be(2);
            }

            using (var refused = await http.PostAsJsonAsync(route, new { sql = "SELECT * FROM pg_class" }))
            {
                refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await refused.Content.ReadAsStringAsync()).Should().Contain(table.StorageName).And.Contain("Region");
            }

            var viewer = await Authorization.AuthzPrincipals.ClientAsync(fixture, Authorization.AuthzPrincipal.Viewer);
            using (var viewed = await viewer.PostAsJsonAsync(route, new { sql }))
                viewed.StatusCode.Should().Be(HttpStatusCode.OK, await viewed.Content.ReadAsStringAsync());
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task RunAsync_MergedTable_ReturnsOnlyTheReadableDocumentsRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, _) = await NewTableAsync(scope);
        try
        {
            Guid open = await NewDocumentAsync(scope, containerId), secret = await NewDocumentAsync(scope, containerId);
            TableInfo merged = await scope.ServiceProvider.GetRequiredService<ITableStore>().CreateAsync(containerId, new TableLoad(
                "Merged", SecretColumns, Rows([["open-a", 1L, open], ["planted", 99L, secret], ["open-b", 2L, open]]),
                SourceDocuments: [open, secret]));
            TableAccess access = new(new HashSet<Guid> { open });
            string[] queries =
            [
                $"SELECT * FROM {merged.StorageName}",
                $"SELECT count(*), sum(c_2) FROM {merged.StorageName}",
                $"SELECT c_1 FROM {merged.StorageName} WHERE c_1 = 'planted' OR c_2 = 99",
            ];

            foreach (TableSqlService service in new[] { (TableSqlService)scope.ServiceProvider.GetRequiredService<ITableSqlService>(), Unchecked(scope) })
            {
                TableQueryResult all = await service.RunAsync(containerId, access, queries[0]);
                TableQueryResult totals = await service.RunAsync(containerId, access, queries[1]);
                TableQueryResult planted = await service.RunAsync(containerId, access, queries[2]);

                all.Rows.Select(r => r[0]).Should().BeEquivalentTo(["open-a", "open-b"]);
                totals.Rows.Single().Should().Equal(2L, 3m);
                planted.Rows.Should().BeEmpty();
            }
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task RunAsync_TableWithHiddenColumns_IsRefusedWithoutNamingIt()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            var store = scope.ServiceProvider.GetRequiredService<ITableStore>();
            TableInfo secret = await store.CreateAsync(containerId,
                new TableLoad("Payroll", SecretColumns, Rows(SecretRows), await NewDocumentAsync(scope, containerId)));
            secret = (await store.SetHiddenColumnsAsync(containerId, secret.Id, ["Salary"]))!;
            var service = scope.ServiceProvider.GetRequiredService<ITableSqlService>();
            TableAccess access = Access(table, secret);

            foreach (string query in new[]
            {
                $"SELECT * FROM {secret.StorageName}",
                $"SELECT c_1 FROM {secret.StorageName}",
                $"SELECT p FROM {secret.StorageName} p",
                $"SELECT s.c_1 FROM {table.StorageName} t JOIN {secret.StorageName} s ON true",
            })
            {
                Func<Task> act = () => service.RunAsync(containerId, access, query);

                string message = (await act.Should().ThrowAsync<TableQueryException>()).Which.Message;
                message.Should().Contain(table.StorageName);
                message.Should().NotContain("Payroll").And.NotContain("Salary").And.NotContain("not a number");
            }
            (await service.RunAsync(containerId, access, $"SELECT count(*) FROM {table.StorageName}")).Rows.Single().Should().Equal(5L);

            Func<Task> onlyHidden = () => service.RunAsync(containerId, Access(secret), $"SELECT c_2 FROM {secret.StorageName}");
            (await onlyHidden.Should().ThrowAsync<TableQueryException>()).Which.Message.Should().Contain("hid columns");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    private TableSqlService Unchecked(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<ITableStore>(),
        fixture.Factory.Services.GetRequiredService<TableReaderLogin>(),
        fixture.Factory.Services.GetRequiredService<TableQueryRunner>(),
        NullLogger<TableSqlService>.Instance)
    {
        CheckQueries = false,
    };

    private async Task<long> AdminCountAsync(TableInfo table)
    {
        await using NpgsqlDataSource admin = NpgsqlDataSource.Create(
            fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!);
        await using NpgsqlCommand count = admin.CreateCommand(
            $"SELECT count(*) FROM {TableDdl.Quote(table.StorageSchema)}.{TableDdl.Quote(table.StorageName)}");
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text));

    private static TableAccess Access(params TableInfo[] tables) => new(tables.Select(t => t.DocumentId!.Value).ToHashSet());

    private static async Task<(IContainerStore Containers, Guid ContainerId, TableInfo Table)> NewTableAsync(AsyncServiceScope scope)
    {
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var container = await containers.CreateAsync(new CreateContainerRequest($"sql-{Guid.NewGuid():N}", null, ContainerKind.Documents));
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

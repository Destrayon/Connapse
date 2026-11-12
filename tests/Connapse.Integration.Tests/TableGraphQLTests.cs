using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-336: agents query a container's tables through GraphQL. Against a real database, the tool answers only from
/// the caller's container, only rows of documents the caller may read, and only visible columns; anything else is an
/// unknown name, and the refusal never names it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class TableGraphQLTests(SharedWebAppFixture fixture)
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
    public async Task RunAsync_FilterOrderAggregate_AnswersFromTheTable()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableGraphQLService>();

            TableGraphQLResult result = await service.RunAsync(containerId, Access(table),
                """
                {
                  top: Sales(where: {Region: {_is_null: false}}, order_by: [{Amount: desc}], limit: 2) { Region Amount }
                  Sales_aggregate(where: {Region: {_is_null: false}}, having: {count: {_gt: 1}}) { Region count sum { Amount } }
                }
                """);

            result.Data.GetProperty("top").EnumerateArray().Select(r => r.GetProperty("Region").GetString()).Should().Equal("east", "north");
            JsonElement north = result.Data.GetProperty("Sales_aggregate").EnumerateArray().Should().ContainSingle().Subject;
            north.GetProperty("Region").GetString().Should().Be("north");
            north.GetProperty("count").GetInt64().Should().Be(2);
            north.GetProperty("sum").GetProperty("Amount").GetDecimal().Should().Be(30.5m);
            result.Notice.Should().Be(TableQueryResult.UntrustedDataNotice);
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Theory]
    [InlineData("{ Secret { Salary } }")]
    [InlineData("{ Secret_aggregate { count } }")]
    [InlineData("{ _schema(table: \"Secret\") }")]
    [InlineData("{ Sales(join: {table: Secret, on: {Region: Salary}}) { Region } }")]
    [InlineData("{ Sales { Region Secret(on: {Region: Salary}) { Salary } } }")]
    public async Task RunAsync_UnreadableTable_IsUnknownAndLeaksNothing(string query)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            await scope.ServiceProvider.GetRequiredService<ITableStore>().CreateAsync(containerId,
                new TableLoad("Secret", SecretColumns, Rows(SecretRows), await NewDocumentAsync(scope, containerId)));
            var service = scope.ServiceProvider.GetRequiredService<ITableGraphQLService>();

            Func<Task> act = () => service.RunAsync(containerId, Access(table), query);

            string message = (await act.Should().ThrowAsync<TableQueryException>()).Which.Message;
            message.Should().NotContain("Bonus").And.NotContain("hidden").And.NotContain("not a number");
            TableGraphQLResult schema = await service.RunAsync(containerId, Access(table), "{ _schema }");
            schema.Data.GetProperty("_schema").GetString().Should().Contain("Sales").And.NotContain("Secret").And.NotContain("Salary");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task RunAsync_TableOfAnotherContainer_IsUnknown()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        var (_, otherId, other) = await NewTableAsync(scope, "Elsewhere");
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableGraphQLService>();
            // The caller may read the other container's document; the table still isn't in this container's schema.
            TableAccess access = Access(table, other);

            Func<Task> read = () => service.RunAsync(containerId, access, "{ Elsewhere { Region } }");
            Func<Task> join = () => service.RunAsync(containerId, access, "{ Sales(join: {table: Elsewhere, on: {Region: Region}}) { Region } }");

            await read.Should().ThrowAsync<TableQueryException>();
            await join.Should().ThrowAsync<TableQueryException>();
            (await service.RunAsync(containerId, access, "{ _schema }")).Data.GetProperty("_schema").GetString()
                .Should().NotContain("Elsewhere");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
            await DeleteAsync(scope, containers, otherId);
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
            await scope.ServiceProvider.GetRequiredService<ITableStore>().CreateAsync(containerId, new TableLoad(
                "Merged", SecretColumns, Rows([["open-a", 1L, open], ["planted", 99L, secret], ["open-b", 2L, open]]),
                SourceDocuments: [open, secret]));
            var service = scope.ServiceProvider.GetRequiredService<ITableGraphQLService>();
            TableAccess access = new(new HashSet<Guid> { open });

            TableGraphQLResult result = await service.RunAsync(containerId, access,
                """
                {
                  all: Merged { Salary }
                  planted: Merged(where: {_or: [{Salary: {_eq: "planted"}}, {Bonus: {_eq: 99}}]}) { Salary }
                  Merged_aggregate { count sum { Bonus } }
                }
                """);

            result.Data.GetProperty("all").EnumerateArray().Select(r => r.GetProperty("Salary").GetString()).Should().BeEquivalentTo(["open-a", "open-b"]);
            result.Data.GetProperty("planted").GetArrayLength().Should().Be(0);
            JsonElement totals = result.Data.GetProperty("Merged_aggregate")[0];
            totals.GetProperty("count").GetInt64().Should().Be(2);
            totals.GetProperty("sum").GetProperty("Bonus").GetDecimal().Should().Be(3m);
            result.Data.GetRawText().Should().NotContain("planted\"");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task RunAsync_HiddenColumn_IsUnknownEverywhere()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, table) = await NewTableAsync(scope);
        try
        {
            var store = scope.ServiceProvider.GetRequiredService<ITableStore>();
            TableInfo payroll = await store.CreateAsync(containerId,
                new TableLoad("Payroll", SecretColumns, Rows(SecretRows), await NewDocumentAsync(scope, containerId)));
            payroll = (await store.SetHiddenColumnsAsync(containerId, payroll.Id, ["Salary"]))!;
            var service = scope.ServiceProvider.GetRequiredService<ITableGraphQLService>();
            TableAccess access = Access(table, payroll);

            foreach (string query in new[]
            {
                "{ Payroll { Salary } }",
                "{ Payroll(where: {Salary: {_eq: \"hidden\"}}) { Bonus } }",
                "{ Payroll(order_by: {Salary: asc}) { Bonus } }",
                "{ Payroll_aggregate { Salary count } }",
                "{ Payroll_aggregate { max { Salary } } }",
                "{ Sales(join: {table: Payroll, on: {Region: Salary}}) { Region } }",
                "{ Sales { Region Payroll(on: {Region: Salary}) { Bonus } } }",
            })
            {
                Func<Task> act = () => service.RunAsync(containerId, access, query);

                (await act.Should().ThrowAsync<TableQueryException>()).Which.Message.Should().NotContain("not a number");
            }

            TableGraphQLResult visible = await service.RunAsync(containerId, access, "{ Payroll(order_by: {Bonus: asc}) { Bonus } _schema }");
            visible.Data.GetProperty("Payroll").EnumerateArray().Select(r => r.GetProperty("Bonus").GetInt64()).Should().Equal(0L, 1L);
            visible.Data.GetProperty("_schema").GetString().Should().Contain("Payroll").And.NotContain("Salary");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task RunAsync_ChildTable_NestsOnlyReadableChildRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, _) = await NewTableAsync(scope);
        try
        {
            var store = scope.ServiceProvider.GetRequiredService<ITableStore>();
            Guid open = await NewDocumentAsync(scope, containerId), secret = await NewDocumentAsync(scope, containerId);
            TableColumn row = new("_row", TableColumnType.WholeNumber), parent = new("_parent_row", TableColumnType.WholeNumber);
            await store.CreateAsync(containerId, new TableLoad("orders", [row, new("id", TableColumnType.Text)],
                Rows([[1L, "A", open], [2L, "B", open]]), SourceDocuments: [open, secret]));
            await store.CreateAsync(containerId, new TableLoad("orders.items", [row, parent, new("sku", TableColumnType.Text)],
                Rows([[1L, 1L, "x", open], [2L, 1L, "planted", secret], [3L, 2L, "z", open]]), SourceDocuments: [open, secret]));
            var service = scope.ServiceProvider.GetRequiredService<ITableGraphQLService>();

            TableGraphQLResult result = await service.RunAsync(containerId, new TableAccess(new HashSet<Guid> { open }),
                "{ orders(order_by: {id: asc}) { id items { sku } } }");

            JsonElement orders = result.Data.GetProperty("orders");
            orders[0].GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sku").GetString()).Should().Equal("x");
            orders[1].GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sku").GetString()).Should().Equal("z");
            result.Data.GetRawText().Should().NotContain("planted");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Surfaces_RestAndMcp_AnswerForViewersAndRefuseBadQueries()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, _) = await NewTableAsync(scope);
        try
        {
            string query = "{ Sales(where: {Units: {_lte: 2}}, order_by: {Units: asc}) { Region Units } }";
            string route = $"/api/containers/{containerId}/tables/graphql";

            using (var ran = await fixture.AdminClient.PostAsJsonAsync(route, new { query }))
            {
                ran.StatusCode.Should().Be(HttpStatusCode.OK, await ran.Content.ReadAsStringAsync());
                using JsonDocument body = JsonDocument.Parse(await ran.Content.ReadAsStringAsync());
                body.RootElement.GetProperty("data").GetProperty("Sales").EnumerateArray()
                    .Select(r => r.GetProperty("Region").GetString()).Should().Equal("north", "north");
                body.RootElement.GetProperty("notice").GetString().Should().Be(TableQueryResult.UntrustedDataNotice);
            }
            await using (var mcp = await CrossSurface.Surfaces.McpAsync(fixture))
            {
                string text = Text(await mcp.CallToolAsync("table_graphql",
                    new Dictionary<string, object?> { ["containerId"] = containerId.ToString(), ["query"] = query }));
                using JsonDocument body = JsonDocument.Parse(text);
                body.RootElement.GetProperty("data").GetProperty("Sales").GetArrayLength().Should().Be(2);
            }

            using (var refused = await fixture.AdminClient.PostAsJsonAsync(route, new { query = "mutation { Sales { Region } }" }))
                refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using (var missing = await fixture.AdminClient.PostAsJsonAsync($"/api/containers/{Guid.NewGuid()}/tables/graphql", new { query }))
                missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var viewer = await Authorization.AuthzPrincipals.ClientAsync(fixture, Authorization.AuthzPrincipal.Viewer);
            using (var viewed = await viewer.PostAsJsonAsync(route, new { query }))
                viewed.StatusCode.Should().Be(HttpStatusCode.OK, await viewed.Content.ReadAsStringAsync());
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text));

    private static TableAccess Access(params TableInfo[] tables) => new(tables.Select(t => t.DocumentId!.Value).ToHashSet());

    private static async Task<(IContainerStore Containers, Guid ContainerId, TableInfo Table)> NewTableAsync(
        AsyncServiceScope scope, string name = "Sales")
    {
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var container = await containers.CreateAsync(new CreateContainerRequest($"gql-{Guid.NewGuid():N}", null, ContainerKind.Documents));
        Guid containerId = Guid.Parse(container.Id);
        TableInfo table = await scope.ServiceProvider.GetRequiredService<ITableStore>()
            .CreateAsync(containerId, new TableLoad(name, Columns, Rows(SalesRows), await NewDocumentAsync(scope, containerId)));
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

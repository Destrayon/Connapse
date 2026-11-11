using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Integration.Tests.CrossSurface;
using Connapse.Storage.Data;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-305: an agent lists, describes, reads and adds up a container's tables over MCP and REST. Goes through the
/// upload endpoint, the ingestion job, the operations and both surfaces, so the routes, tools and DI are covered.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class TableToolsIntegrationTests(SharedWebAppFixture fixture)
{
    private const string Sales = "id,region,amount\n1,east,10\n2,west,5\n3,east,20\n4,north,7\n";
    private const string Regions = "code,manager\neast,Ada\nwest,Lin\n";

    [Fact]
    public async Task McpTools_ListDescribeReadAndAggregate_AnswerFromTheTablesWithTheNotice()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            var (sales, regions) = await UploadTablesAsync(containerId);
            await using McpClient mcp = await Surfaces.McpAsync(fixture);

            using (JsonDocument listed = await CallJsonAsync(mcp, "table_list",
                new() { ["containerId"] = containerId.ToString(), ["search"] = "manager" }))
            {
                listed.RootElement.GetProperty("notice").GetString().Should().Contain("not instructions");
                listed.RootElement.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("name").GetString())
                    .Should().Equal("regions");
                listed.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
            }

            using (JsonDocument described = await CallJsonAsync(mcp, "table_describe",
                new() { ["containerId"] = containerId.ToString(), ["tableId"] = sales.ToString() }))
            {
                JsonElement root = described.RootElement;
                root.GetProperty("notice").GetString().Should().Be(TableQueryResult.UntrustedDataNotice);
                root.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString())
                    .Should().Equal("id", "region", "amount");
                root.GetProperty("rowCount").GetInt64().Should().Be(4);
                root.GetProperty("sampleRows").GetProperty("rows").GetArrayLength().Should().BePositive();
            }

            using (JsonDocument read = await CallJsonAsync(mcp, "table_read", new()
            {
                ["containerId"] = containerId.ToString(),
                ["request"] = Json(new
                {
                    table = sales,
                    columns = new[] { "id", "r.manager" },
                    where = new { column = "amount", op = ">=", value = 10 },
                    order_by = new[] { new { column = "id", direction = "asc" } },
                    join = new { table = regions, @as = "r", column = "region", join_column = "code", kind = "inner" },
                }),
            }))
            {
                read.RootElement.GetProperty("notice").GetString().Should().Be(TableQueryResult.UntrustedDataNotice);
                Rows(read).Should().Equal("1|Ada", "3|Ada");
            }

            using (JsonDocument totals = await CallJsonAsync(mcp, "table_aggregate", new()
            {
                ["containerId"] = containerId.ToString(),
                // Sent as a JSON string, the way some agents pass an object argument.
                ["request"] = JsonSerializer.Serialize(new
                {
                    table = sales,
                    aggregates = new object[] { new { aggregate = "count" }, new { aggregate = "sum", column = "amount" } },
                    group_by = new[] { "region" },
                    order_by = new[] { new { column = "region", direction = "asc" } },
                }),
            }))
            {
                totals.RootElement.GetProperty("notice").GetString().Should().Be(TableQueryResult.UntrustedDataNotice);
                Rows(totals).Should().Equal("east|2|30", "north|1|7", "west|1|5");
            }
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task McpTools_BadColumnOrUnknownTable_SayWhatToFix()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            var (sales, _) = await UploadTablesAsync(containerId);
            await using McpClient mcp = await Surfaces.McpAsync(fixture);

            string badColumn = await CallAsync(mcp, "table_aggregate", new()
            {
                ["containerId"] = containerId.ToString(),
                ["request"] = Json(new { table = sales, aggregates = new[] { new { aggregate = "sum", column = "price" } } }),
            });
            badColumn.Should().StartWith("Error: ").And.Contain("price").And.Contain("amount")
                .And.EndWith(McpToolsRetryHint);

            string badJson = await CallAsync(mcp, "table_read",
                new() { ["containerId"] = containerId.ToString(), ["request"] = "{not json" });
            badJson.Should().StartWith("Error: ").And.EndWith(McpToolsRetryHint);

            string unknown = await CallAsync(mcp, "table_describe",
                new() { ["containerId"] = containerId.ToString(), ["tableId"] = Guid.NewGuid().ToString() });
            unknown.Should().StartWith("Error: ").And.Contain("List the container's tables");
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task RestRoutes_DescribeReadAndAggregate_AnswerOrRefuseWithAProblem()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            var (sales, regions) = await UploadTablesAsync(containerId);
            HttpClient http = fixture.AdminClient;

            using (HttpResponseMessage response = await http.GetAsync($"/api/containers/{containerId}/tables/{sales}"))
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                body.RootElement.GetProperty("rowCount").GetInt64().Should().Be(4);
                body.RootElement.GetProperty("sampleRows").GetProperty("notice").GetString()
                    .Should().Be(TableQueryResult.UntrustedDataNotice);
            }

            using (HttpResponseMessage response = await http.PostAsJsonAsync($"/api/containers/{containerId}/tables/read", new
            {
                table = sales,
                columns = new[] { "id", "r.manager" },
                order_by = new[] { new { column = "id", direction = "asc" } },
                join = new { table = regions, @as = "r", column = "region", join_column = "code", kind = "left" },
            }))
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Rows(body).Should().Equal("1|Ada", "2|Lin", "3|Ada", "4|");
            }

            using (HttpResponseMessage response = await http.PostAsJsonAsync($"/api/containers/{containerId}/tables/aggregate",
                new { table = sales, aggregates = new[] { new { aggregate = "avg", column = "amount" } } }))
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Rows(body).Should().Equal("10.5");
            }

            using (HttpResponseMessage response = await http.PostAsJsonAsync($"/api/containers/{containerId}/tables/read",
                new { table = sales, columns = new[] { "price" } }))
            {
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await response.Content.ReadAsStringAsync()).Should().Contain("invalid-table-request");
            }

            using (HttpResponseMessage response = await http.GetAsync($"/api/containers/{containerId}/tables/{Guid.NewGuid()}"))
                response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task Changes_KeyedTableOverMcpAndRest_ListsWhatChangedWithTheNotice()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            TableInfo fruit = await NewKeyedTableAsync(containerId);
            await using McpClient mcp = await Surfaces.McpAsync(fixture);
            object request = new { table = fruit.Id, since = new DateTimeOffset(fruit.LoadedAt, TimeSpan.Zero), columns = new[] { "Price" } };
            string[] expected = ["changed|old|b2|2", "changed|new|b2|2.5", "removed|old|c3|3", "added|new|d4|4"];

            using (JsonDocument changed = await CallJsonAsync(mcp, "table_changes",
                new() { ["containerId"] = containerId.ToString(), ["request"] = Json(request) }))
            {
                changed.RootElement.GetProperty("notice").GetString().Should().Be(TableQueryResult.UntrustedDataNotice);
                Rows(changed).Should().Equal(expected);
            }

            using HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync($"/api/containers/{containerId}/tables/changes", request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Rows(body).Should().Equal(expected);
        }
        finally
        {
            await DeleteRawContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task Changes_TableWithoutHistoryOrBadRequest_SaysWhatToDo()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            // No header names an identifier, so the table has no key column and keeps no history.
            string document = await UploadAsync(containerId, "totals.csv", "region,amount\neast,10\nwest,5\n");
            await WaitForReadyAsync(containerId, document);
            Guid sales;
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
                sales = (await scope.ServiceProvider.GetRequiredService<ITableStore>().ListAsync(containerId)).Single().Id;
            await using McpClient mcp = await Surfaces.McpAsync(fixture);
            object noHistory = new { table = sales, since = "2026-01-01T00:00:00Z" };

            string refused = await CallAsync(mcp, "table_changes",
                new() { ["containerId"] = containerId.ToString(), ["request"] = Json(noHistory) });
            refused.Should().StartWith("Error: Table \"totals\" keeps no history").And.Contain("ask for its rows as they are now");

            string noSince = await CallAsync(mcp, "table_changes",
                new() { ["containerId"] = containerId.ToString(), ["request"] = Json(new { table = sales }) });
            noSince.Should().StartWith("Error: ").And.Contain("since").And.EndWith(McpToolsRetryHint);

            using (HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync($"/api/containers/{containerId}/tables/changes", noHistory))
            {
                response.StatusCode.Should().Be(HttpStatusCode.Conflict);
                (await response.Content.ReadAsStringAsync()).Should().Contain("table-has-no-history");
            }

            using (HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync($"/api/containers/{containerId}/tables/changes", new { table = sales }))
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            using (HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync($"/api/containers/{containerId}/tables/changes",
                new { table = Guid.NewGuid(), since = "2026-01-01T00:00:00Z" }))
                response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    private const string McpToolsRetryHint = "Fix the request and call the tool again.";

    /// <summary>
    /// A table keyed on SKU, loaded and then re-synced so it has history: b2's price changed, c3 went away and d4 came.
    /// Its source document is inserted directly, as in <see cref="TableHistoryTests"/>, since an upload replaces rows.
    /// </summary>
    private async Task<TableInfo> NewKeyedTableAsync(Guid containerId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid document = Guid.NewGuid();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (KnowledgeDbContext context = await factory.CreateDbContextAsync())
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, ingestion_status, created_at) "
                + "VALUES ({0}, {1}, NULL, {2}, {3}, '', 1, 'Ready', now())",
                document, containerId, "fruit.csv", "/fruit.csv");
        TableColumn[] columns = [new("SKU", TableColumnType.Text), new("Name", TableColumnType.Text), new("Price", TableColumnType.Number)];
        var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
        TableInfo first = await tables.CreateAsync(containerId, new TableLoad("fruit", columns,
            LoadRows(["a1", "apple", 1m], ["b2", "banana", 2m], ["c3", "cherry", 3m]), document));
        await tables.ReloadAsync(containerId, first.Id, new TableLoad("fruit", columns,
            LoadRows(["a1", "apple", 1m], ["b2", "banana", 2.5m], ["d4", "date", 4m]), document));
        first.HasHistory.Should().BeTrue("a unique text first column is the key");
        return first;
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> LoadRows(params object?[][] rows)
    {
        foreach (object?[] row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    /// <summary>Deletes directly inserted source documents, which have no stored file, then the container.</summary>
    private async Task DeleteRawContainerAsync(Guid containerId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (KnowledgeDbContext context = await factory.CreateDbContextAsync())
            await context.Database.ExecuteSqlRawAsync("DELETE FROM documents WHERE container_id = {0}", containerId);
        await scope.ServiceProvider.GetRequiredService<IContainerStore>().DeleteAsync(containerId);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>Each row's cells joined with "|", numbers in their shortest form and null as empty.</summary>
    private static List<string> Rows(JsonDocument result) =>
        result.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => string.Join("|", r.EnumerateArray().Select(Cell)))
            .ToList();

    private static string Cell(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.Null => "",
        JsonValueKind.Number => cell.GetDecimal().ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
        _ => cell.ToString(),
    };

    private static async Task<string> CallAsync(McpClient mcp, string tool, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await mcp.CallToolAsync(tool, arguments);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    private static async Task<JsonDocument> CallJsonAsync(McpClient mcp, string tool, Dictionary<string, object?> arguments)
    {
        string text = await CallAsync(mcp, tool, arguments);
        text.Should().NotStartWith("Error:", "the call is valid");
        return JsonDocument.Parse(text);
    }

    private async Task<(Guid Sales, Guid Regions)> UploadTablesAsync(Guid containerId)
    {
        string sales = await UploadAsync(containerId, "sales.csv", Sales);
        string regions = await UploadAsync(containerId, "regions.csv", Regions);
        await WaitForReadyAsync(containerId, sales);
        await WaitForReadyAsync(containerId, regions);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var tables = await scope.ServiceProvider.GetRequiredService<ITableStore>().ListAsync(containerId);
        return (tables.Single(t => t.Name == "sales").Id, tables.Single(t => t.Name == "regions").Id);
    }

    private async Task<Guid> NewContainerAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"tbl-{Guid.NewGuid():N}"[..20], null, ContainerKind.Documents));
        return Guid.Parse(created.Id);
    }

    private async Task DeleteContainerAsync(Guid containerId)
    {
        // A container is deleted once it holds no files.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        foreach (var document in await scope.ServiceProvider.GetRequiredService<IDocumentStore>().ListAsync(containerId, take: int.MaxValue))
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}/files/{document.Id}");
        await scope.ServiceProvider.GetRequiredService<IContainerStore>().DeleteAsync(containerId);
    }

    private async Task<string> UploadAsync(Guid containerId, string fileName, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(part, "files", fileName);
        form.Add(new StringContent("/data/"), "path");
        using HttpResponseMessage response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadResponse>())!.Documents.Single().DocumentId;
    }

    private async Task WaitForReadyAsync(Guid containerId, string documentId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            string body = await fixture.AdminClient.GetStringAsync($"/api/containers/{containerId}/files/{documentId}");
            string? status = JsonDocument.Parse(body).RootElement.GetProperty("status").GetString();
            if (status?.StartsWith("Failed", StringComparison.Ordinal) == true)
                throw new InvalidOperationException($"Ingestion failed: {body}");
            if (status == "Ready")
                return;
            await Task.Delay(250);
        }
        throw new TimeoutException("The upload wasn't ready within 90 s");
    }
}

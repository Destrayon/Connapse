using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Connapse.Ingestion.Tests.Tables.XlsxFixture;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-308: an Excel workbook uploaded to a container loads one typed table per visible sheet, and a flat JSON array
/// loads as one table; both stay searchable as text. Goes through the upload endpoint and the ingestion job.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SpreadsheetTableUploadTests(SharedWebAppFixture fixture)
{
    private static byte[] Workbook(params SheetSpec[] sheets) => Build(sheets).ToArray();

    [Fact]
    public async Task Upload_Workbook_LoadsATablePerSheetAndIsFoundByTextSearch()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            byte[] book = Workbook(
                new SheetSpec("Fruit", $"<sheetData>{Row(1, "Harvest 2024")}{Row(2, "name", "kilos")}{Row(3, "persimmon", 12)}{Row(4, "quince", 7.5)}</sheetData>"),
                new SheetSpec("Totals",
                    $"<sheetData>{Row(1, "a", "total")}<row r=\"2\"><c r=\"A2\"><v>1</v></c><c r=\"B2\"><f>A2*2</f></c></row>{Row(3, 2, 4)}</sheetData>"));
            string id = await UploadAsync(containerId, "harvest.xlsx", book);
            await WaitForReadyAsync(containerId, id);

            var tables = await TablesAsync(containerId);
            tables.Select(t => t.Name).Should().Equal("harvest - Fruit", "harvest - Totals");
            var fruit = tables[0];
            fruit.Columns.Select(c => (c.Name, c.Type)).Should().Equal(("name", TableColumnType.Text), ("kilos", TableColumnType.Number));
            fruit.RowCount.Should().Be(2);
            fruit.NeedsReview.Should().BeFalse();
            fruit.Description.Should().Contain("Harvest 2024");
            var totals = tables[1];
            totals.RowCount.Should().Be(2);
            totals.NeedsReview.Should().BeTrue();
            totals.Rejects.Should().ContainSingle().Which.Reason.Should().Contain("without a result");

            var document = await DocumentAsync(id);
            document.Metadata["TableId"].Should().Be(fruit.Id.ToString());
            document.Metadata["TableIds"].Should().Be($"{fruit.Id},{totals.Id}");
            document.Metadata["ParserWarnings"].Should().Contain("Open and save the file in Excel");
            (await KeywordHitsAsync(containerId, "persimmon")).Should().Contain(id);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task Reupload_WorkbookWithASheetRemoved_KeepsTheOtherTableAndDropsTheRemovedOnes()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            SheetSpec sales = new("Sales", $"<sheetData>{Row(1, "id", "amount")}{Row(2, 1, 5)}</sheetData>");
            SheetSpec costs = new("Costs", $"<sheetData>{Row(1, "item", "cost")}{Row(2, "pen", 2)}</sheetData>");
            string id = await UploadAsync(containerId, "book.xlsx", Workbook(sales, costs));
            await WaitForReadyAsync(containerId, id);
            var before = await TablesAsync(containerId);
            before.Should().HaveCount(2);

            SheetSpec moreSales = new("Sales", $"<sheetData>{Row(1, "id", "amount")}{Row(2, 1, 5)}{Row(3, 2, 6)}</sheetData>");
            (await UploadAsync(containerId, "book.xlsx", Workbook(moreSales))).Should().Be(id);
            // Tables load before the document's metadata is written, and the upload clears the old metadata, so wait for both.
            await WaitForAsync(async () => (await TablesAsync(containerId)) is [{ Version: 2 }]
                && (await DocumentAsync(id)).Metadata.ContainsKey("TableId"));

            var after = (await TablesAsync(containerId)).Single();
            after.Id.Should().Be(before.Single(t => t.Name == "book - Sales").Id);
            after.RowCount.Should().Be(2);
            var document = await DocumentAsync(id);
            document.Metadata["TableId"].Should().Be(after.Id.ToString());
            document.Metadata.Should().NotContainKey("TableIds");
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task Upload_FlatJsonArray_LoadsAsATable()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            string people = await UploadAsync(containerId, "people.json",
                Encoding.UTF8.GetBytes("""[{"name": "Ann", "joined": "2024-01-31", "age": 41}, {"name": "Bob", "age": 7}]"""));
            await WaitForReadyAsync(containerId, people);

            var table = (await TablesAsync(containerId)).Single();
            table.Name.Should().Be("people");
            table.DocumentId.Should().Be(Guid.Parse(people));
            table.Columns.Select(c => (c.Name, c.Type)).Should().Equal(
                ("name", TableColumnType.Text), ("joined", TableColumnType.Date), ("age", TableColumnType.WholeNumber));
            table.RowCount.Should().Be(2);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task Upload_NestedJson_LoadsParentAndChildTablesAnAgentCanFilterCountAndJoin()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            string id = await UploadAsync(containerId, "orders.json", Encoding.UTF8.GetBytes("""
                [
                  {"id": 1, "customer": {"name": "Ann"}, "lines": [{"sku": "a", "qty": 2}, {"sku": "b", "qty": 1}], "grid": [[1, 2], [3]]},
                  {"id": 2, "customer": {"name": "Bob"}, "lines": [{"sku": "a", "qty": 5}]}
                ]
                """));
            await WaitForReadyAsync(containerId, id);

            var tables = (await TablesAsync(containerId)).ToDictionary(t => t.Name);
            tables.Keys.Should().BeEquivalentTo("orders", "orders.lines", "orders.grid");
            tables["orders"].Columns.Select(c => c.Name).Should().Equal("_row", "id", "customer.name");
            tables["orders.grid"].Columns[^1].Type.Should().Be(TableColumnType.Json);
            TableInfo orders = tables["orders"], lines = tables["orders.lines"], grid = tables["orders.grid"];

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            TableAccess access = new(new HashSet<Guid> { Guid.Parse(id) });

            TableQueryResult joined = await service.ReadAsync(containerId, access, new TableReadRequest(
                lines.Id, ["o.customer.name", "sku", "qty"],
                Where: new TableCondition(new TableField("sku"), TableFilterOperator.Equal, ["a"]),
                OrderBy: [new TableOrder(new TableField("qty"))],
                Join: new TableJoin(orders.Id, "o", "_parent_row", "_row")));
            joined.Rows.Select(r => (r[0], r[2])).Should().Equal(("Ann", (object)2L), ("Bob", 5L));

            TableQueryResult counted = await service.AggregateAsync(containerId, access, new TableAggregateRequest(
                lines.Id, [new TableField(null, TableAggregateFunction.Count)], ["sku"],
                OrderBy: [new TableOrder(new TableField("sku"))]));
            counted.Rows.Select(r => (r[0], r[1])).Should().Equal(("a", (object)2L), ("b", 1L));

            TableQueryResult json = await service.ReadAsync(containerId, access, new TableReadRequest(
                grid.Id, ["value"], Where: new TableCondition(new TableField("value"), TableFilterOperator.Contains, ["3"])));
            json.Rows.Should().ContainSingle().Which[0].Should().Be("[3]");

            TableQueryResult sql = await scope.ServiceProvider.GetRequiredService<ITableSqlService>().RunAsync(containerId, access,
                $"SELECT c_2 AS i, jsonb_array_length(c_3) AS n, c_3->>0 AS first FROM {grid.StorageName} ORDER BY c_2");
            sql.Rows.Select(r => (r[0], r[1], r[2])).Should().Equal((0L, (object)2, (object)"1"), (1L, 1, "3"));
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    private async Task<List<string?>> KeywordHitsAsync(Guid containerId, string query)
    {
        using HttpResponseMessage response = await fixture.AdminClient.GetAsync(
            $"/api/containers/{containerId}/search?q={Uri.EscapeDataString(query)}&mode=Keyword&topK=5");
        response.EnsureSuccessStatusCode();
        using JsonDocument found = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return found.RootElement.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("documentId").GetString()).ToList();
    }

    private async Task<Guid> NewContainerAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"xls-{Guid.NewGuid():N}"[..20], null, ContainerKind.Documents));
        return Guid.Parse(created.Id);
    }

    private async Task DeleteContainerAsync(Guid containerId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        foreach (var document in await scope.ServiceProvider.GetRequiredService<IDocumentStore>().ListAsync(containerId, take: int.MaxValue))
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}/files/{document.Id}");
        await scope.ServiceProvider.GetRequiredService<IContainerStore>().DeleteAsync(containerId);
    }

    private async Task<IReadOnlyList<TableInfo>> TablesAsync(Guid containerId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITableStore>().ListAsync(containerId);
    }

    private async Task<Document> DocumentAsync(string documentId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDocumentStore>().GetAsync(documentId))!;
    }

    private async Task<string> UploadAsync(Guid containerId, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".json")
            ? "application/json"
            : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(part, "files", fileName);
        form.Add(new StringContent("/data/"), "path");
        using HttpResponseMessage response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadResponse>())!.Documents.Single().DocumentId;
    }

    private async Task WaitForReadyAsync(Guid containerId, string documentId)
    {
        await WaitForAsync(async () =>
        {
            string body = await fixture.AdminClient.GetStringAsync($"/api/containers/{containerId}/files/{documentId}");
            string? status = JsonDocument.Parse(body).RootElement.GetProperty("status").GetString();
            if (status?.StartsWith("Failed", StringComparison.Ordinal) == true)
                throw new InvalidOperationException($"Ingestion failed: {body}");
            return status == "Ready";
        });
    }

    private static async Task WaitForAsync(Func<Task<bool>> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (await done())
                return;
            await Task.Delay(250);
        }
        throw new TimeoutException("The condition wasn't met within 90 s");
    }
}

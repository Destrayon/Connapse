using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-302: a CSV uploaded to a container becomes a typed table of that container, one per file, beside its other
/// files. Goes through the upload endpoint and the ingestion job, so the routing and the DI wiring are covered too.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CsvTableUploadTests(SharedWebAppFixture fixture)
{
    private const string Sales = "id,day,amount,paid\n1,2024-01-31,\"1,234.50\",true\n2,2024-02-01,7.25,false\n3,2024-02-02,n/a,true\n";

    [Fact]
    public async Task Upload_CsvToADocumentsContainer_BecomesATypedTableBesideItsOtherFiles()
    {
        Guid containerId = await NewContainerAsync(ContainerKind.Documents);
        try
        {
            await UploadAsync(containerId, "notes.md", "# Notes\n\nNot a table.");
            string sales = await UploadAsync(containerId, "sales.csv", Sales);
            string costs = await UploadAsync(containerId, "costs.csv", "item,cost\npen,2\n");
            await WaitForReadyAsync(containerId, sales);
            await WaitForReadyAsync(containerId, costs);

            var tables = await TablesAsync(containerId);
            tables.Select(t => t.Name).Should().Equal("costs", "sales");
            var table = tables.Single(t => t.Name == "sales");
            table.DocumentId.Should().Be(Guid.Parse(sales));
            table.Columns.Select(c => (c.Name, c.Type)).Should().Equal(
                ("id", TableColumnType.WholeNumber), ("day", TableColumnType.Date), ("amount", TableColumnType.Text),
                ("paid", TableColumnType.Boolean));
            table.RowCount.Should().Be(3);

            var rows = await QueryAsync($"SELECT c_1, c_2, c_4 FROM \"{table.StorageSchema}\".\"{table.StorageName}\" ORDER BY c_1",
                r => (r.GetInt64(0), r.GetFieldValue<DateOnly>(1), r.GetBoolean(2)));
            rows[0].Should().Be((1L, new DateOnly(2024, 1, 31), true));

            var document = await DocumentAsync(sales);
            document.Metadata["TableId"].Should().Be(table.Id.ToString());
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task Upload_CsvToADocumentsContainer_IsFoundByTextSearchAndLoadsAsATable()
    {
        Guid containerId = await NewContainerAsync(ContainerKind.Documents);
        try
        {
            string fruit = await UploadAsync(containerId, "fruit.csv", "name,origin,kilos\npersimmon,Okayama,12\nquince,Isfahan,7\n");
            await WaitForReadyAsync(containerId, fruit);

            using HttpResponseMessage response = await fixture.AdminClient.GetAsync(
                $"/api/containers/{containerId}/search?q=persimmon+Okayama&mode=Keyword&topK=5");
            response.EnsureSuccessStatusCode();
            using JsonDocument found = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            found.RootElement.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("documentId").GetString())
                .Should().Contain(fruit, "a CSV in a documents container keeps its text passages");

            var table = (await TablesAsync(containerId)).Single();
            table.DocumentId.Should().Be(Guid.Parse(fruit));
            table.RowCount.Should().Be(2);
            (await DocumentAsync(fruit)).Metadata["TableId"].Should().Be(table.Id.ToString());
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task ListTablesRoute_ContainerWithFilesAndSeveralCsvs_PagesThroughItsTablesByName()
    {
        Guid containerId = await NewContainerAsync(ContainerKind.Documents);
        try
        {
            await UploadAsync(containerId, "notes.md", "# Notes\n\nNot a table.");
            string[] ids =
            [
                await UploadAsync(containerId, "orders.csv", "id,total\n1,5\n"),
                await UploadAsync(containerId, "costs.csv", "item,cost\npen,2\nink,3\n"),
                await UploadAsync(containerId, "stock.csv", "sku,count\nA1,9\n"),
            ];
            foreach (string id in ids)
                await WaitForReadyAsync(containerId, id);

            using JsonDocument first = await GetJsonAsync($"/api/containers/{containerId}/tables?take=2");
            using JsonDocument second = await GetJsonAsync($"/api/containers/{containerId}/tables?skip=2&take=2");

            Names(first).Should().Equal("costs", "orders");
            first.RootElement.GetProperty("totalCount").GetInt32().Should().Be(3);
            first.RootElement.GetProperty("hasMore").GetBoolean().Should().BeTrue();
            Names(second).Should().Equal("stock");
            second.RootElement.GetProperty("hasMore").GetBoolean().Should().BeFalse();

            JsonElement costs = first.RootElement.GetProperty("items")[0];
            costs.GetProperty("documentId").GetString().Should().Be(ids[1]);
            costs.GetProperty("rowCount").GetInt64().Should().Be(2);
            costs.GetProperty("columns").GetArrayLength().Should().Be(2);
            costs.TryGetProperty("storageSchema", out _).Should().BeFalse("where a table is stored is not the caller's business");
            costs.TryGetProperty("storageName", out _).Should().BeFalse();
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task ListTablesRoute_BadPageMissingOrMemoryContainer_AnswersAProblem()
    {
        Guid documents = await NewContainerAsync(ContainerKind.Documents);
        Guid memory = await NewContainerAsync(ContainerKind.Memory);
        try
        {
            (await fixture.AdminClient.GetAsync($"/api/containers/{documents}/tables?take=0")).StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
            (await fixture.AdminClient.GetAsync($"/api/containers/{documents}/tables?take=201")).StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
            (await fixture.AdminClient.GetAsync($"/api/containers/{Guid.NewGuid()}/tables")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
            (await fixture.AdminClient.GetAsync($"/api/containers/{memory}/tables")).StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);

            using JsonDocument empty = await GetJsonAsync($"/api/containers/{documents}/tables");
            empty.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
            empty.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        }
        finally
        {
            await DeleteContainerAsync(documents);
            await DeleteContainerAsync(memory);
        }
    }

    [Fact]
    public async Task Upload_RowThatDoesNotFit_IsListedLeftOutAndWarnedAbout()
    {
        // Small on purpose: the CSV's text is embedded too, and a bad cell past the type sample is the reader's own
        // test (CsvTableReaderTests). This proves the upload keeps the reject list and says so on the document.
        Guid containerId = await NewContainerAsync(ContainerKind.Documents);
        try
        {
            string id = await UploadAsync(containerId, "readings.csv", "id,amount\n1,3\n2,6,9\n3,9\n");
            await WaitForReadyAsync(containerId, id);

            var table = (await TablesAsync(containerId)).Single();
            table.RowCount.Should().Be(2);
            table.NeedsReview.Should().BeTrue();
            table.Rejects.Should().ContainSingle().Which.Should().Be(new TableReject(3, 0, "2,6,9", "has 3 fields, expected 2"));
            (await DocumentAsync(id)).Metadata["ParserWarnings"].Should().Contain("1 row(s) didn't fit");
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task Reupload_SamePath_ReloadsItsOwnTable()
    {
        Guid containerId = await NewContainerAsync(ContainerKind.Documents);
        try
        {
            string first = await UploadAsync(containerId, "sales.csv", Sales);
            await WaitForReadyAsync(containerId, first);
            var before = (await TablesAsync(containerId)).Single();

            string second = await UploadAsync(containerId, "sales.csv", "id,amount\n1,10\n2,20\n3,30\n4,40\n");
            second.Should().Be(first);
            await WaitForAsync(async () => (await TablesAsync(containerId)).Single().Version == 2);

            var after = (await TablesAsync(containerId)).Single();
            after.Id.Should().Be(before.Id);
            after.RowCount.Should().Be(4);
            after.Columns.Select(c => c.Type).Should().Equal(TableColumnType.WholeNumber, TableColumnType.WholeNumber);
            after.StorageName.Should().NotBe(before.StorageName);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task DeleteFile_RetiresItsTable()
    {
        Guid containerId = await NewContainerAsync(ContainerKind.Documents);
        try
        {
            string id = await UploadAsync(containerId, "sales.csv", Sales);
            await WaitForReadyAsync(containerId, id);
            var table = (await TablesAsync(containerId)).Single();

            var response = await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}/files/{id}");
            response.EnsureSuccessStatusCode();

            (await TablesAsync(containerId)).Should().BeEmpty();
            var retired = await QueryAsync($"SELECT count(*) FROM retired_tables WHERE storage_name = '{table.StorageName}'", r => r.GetInt64(0));
            retired.Should().Equal(1L);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task Upload_LargeCsv_LoadsEveryRowButIndexesOnlyASummaryAsText()
    {
        // CON-329: every row of a 60,000-row CSV used to be embedded, which took over ten minutes on two CPUs. Its
        // text is now a summary and its first rows, so it is Ready within the 90 s wait.
        Guid containerId = await NewContainerAsync(ContainerKind.Documents);
        try
        {
            var csv = new StringBuilder("id,customer,amount\n");
            for (int i = 1; i <= 60_000; i++)
                csv.Append(i).Append(",customer-").Append(i).Append(',').Append(i % 500).Append('\n');
            string id = await UploadAsync(containerId, "ledger.csv", csv.ToString());
            await WaitForReadyAsync(containerId, id);

            var table = (await TablesAsync(containerId)).Single();
            table.RowCount.Should().Be(60_000);
            (await DocumentAsync(id)).Metadata["TableId"].Should().Be(table.Id.ToString());
            var chunks = await QueryAsync($"SELECT count(*) FROM chunks WHERE document_id = '{id}'", r => r.GetInt64(0));
            chunks.Single().Should().BeInRange(1, 20, "a large CSV's text is its summary, not its rows");

            (await KeywordHitsAsync(containerId, "customer-7")).Should().Contain(id, "its first rows are in the summary");
            (await KeywordHitsAsync(containerId, "59999")).Should().NotContain(id, "later rows are in the table only");
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task Upload_SmallCsv_KeepsEveryRowInTextSearch()
    {
        Guid containerId = await NewContainerAsync(ContainerKind.Documents);
        try
        {
            var csv = new StringBuilder("id,city\n");
            for (int i = 1; i <= 300; i++)
                csv.Append(i).Append(",town").Append(i).Append('\n');
            csv.Append("301,Ouagadougou\n");
            string id = await UploadAsync(containerId, "cities.csv", csv.ToString());
            await WaitForReadyAsync(containerId, id);

            (await KeywordHitsAsync(containerId, "Ouagadougou")).Should().Contain(id, "a small CSV's last row is still searchable text");
            (await TablesAsync(containerId)).Single().RowCount.Should().Be(301);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    private async Task<List<string?>> KeywordHitsAsync(Guid containerId, string query)
    {
        using JsonDocument found = await GetJsonAsync(
            $"/api/containers/{containerId}/search?q={Uri.EscapeDataString(query)}&mode=Keyword&topK=5");
        return found.RootElement.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("documentId").GetString()).ToList();
    }


    private async Task<Guid> NewContainerAsync(ContainerKind kind)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"csv-{Guid.NewGuid():N}"[..20], null, kind));
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

    private async Task<IReadOnlyList<TableInfo>> TablesAsync(Guid containerId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITableStore>().ListAsync(containerId);
    }

    private async Task<JsonDocument> GetJsonAsync(string url)
    {
        using HttpResponseMessage response = await fixture.AdminClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static List<string?> Names(JsonDocument page) =>
        page.RootElement.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();

    private async Task<Document> DocumentAsync(string documentId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDocumentStore>().GetAsync(documentId))!;
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Func<DbDataReader, T> read)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        List<T> rows = [];
        while (await reader.ReadAsync())
            rows.Add(read(reader));
        return rows;
    }

    private async Task<HttpResponseMessage> PostFileAsync(Guid containerId, string fileName, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".csv") ? "text/csv" : "text/markdown");
        form.Add(part, "files", fileName);
        form.Add(new StringContent("/data/"), "path");
        return await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);
    }

    private async Task<string> UploadAsync(Guid containerId, string fileName, string text)
    {
        var response = await PostFileAsync(containerId, fileName, text);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadResponse>())!.Documents.Single().DocumentId;
    }

    private async Task WaitForReadyAsync(Guid containerId, string documentId)
    {
        string body = "";
        await WaitForAsync(async () =>
        {
            body = await fixture.AdminClient.GetStringAsync($"/api/containers/{containerId}/files/{documentId}");
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

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
    public async Task Upload_BadCellPastTheSample_IsListedAndItsRowLeftOut()
    {
        // A tables container, so the 60,000 rows aren't embedded as text too; the table load is the same either way.
        Guid containerId = await NewContainerAsync(ContainerKind.Tables);
        try
        {
            const int bad = 52_051;
            var csv = new StringBuilder("id,amount\n");
            for (int i = 1; i <= 60_000; i++)
                csv.Append(i).Append(',').Append(i == bad ? "n/a" : (i * 3).ToString()).Append('\n');

            string id = await UploadAsync(containerId, "readings.csv", csv.ToString());
            await WaitForReadyAsync(containerId, id);

            var table = (await TablesAsync(containerId)).Single();
            table.RowCount.Should().Be(59_999);
            table.NeedsReview.Should().BeTrue();
            table.Rejects.Should().ContainSingle().Which.Should().Be(new TableReject(bad + 1, 2, "n/a", "not a whole number"));
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
    public async Task Upload_ToATablesContainer_TakesCsvAndRefusesOtherFiles()
    {
        Guid containerId = await NewContainerAsync(ContainerKind.Tables);
        try
        {
            string id = await UploadAsync(containerId, "sales.csv", Sales);
            await WaitForReadyAsync(containerId, id);
            (await TablesAsync(containerId)).Should().ContainSingle().Which.Name.Should().Be("sales");

            var refused = await PostFileAsync(containerId, "notes.md", "# Notes");
            refused.IsSuccessStatusCode.Should().BeFalse();
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
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

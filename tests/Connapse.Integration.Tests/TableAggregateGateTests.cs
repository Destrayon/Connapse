using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Integration.Tests.CrossSurface;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Connapse.Integration.Tests;

/// <summary>
/// The tables container's speed bar (CON-E6, CON-307): <c>table_aggregate</c> answers at p95 under 500 ms on a
/// 100,000-row table. The CSV goes through the upload endpoint and ingestion, so the table carries its document and
/// every call is permission-trimmed as an agent's would be; each call is timed over MCP, round trip included. One
/// round of the requests warms the pool and the JIT and isn't timed. The category runs in a CI step of its own.
/// </summary>
[Trait("Category", "Gate")]
[Collection("Integration Tests")]
public class TableAggregateGateTests(SharedWebAppFixture fixture, ITestOutputHelper output)
{
    private const int Rows = 100_000;
    private const int Rounds = 20;
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Aggregate_100kRows_P95Under500Milliseconds()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            Guid table = await UploadAsync(containerId);
            await using McpClient mcp = await Surfaces.McpAsync(fixture);
            List<object> requests = Requests(table);

            foreach (object request in requests)
                await AggregateAsync(mcp, containerId, request);
            List<double> timings = [];
            for (int round = 0; round < Rounds; round++)
            {
                foreach (object request in requests)
                {
                    var timer = Stopwatch.StartNew();
                    await AggregateAsync(mcp, containerId, request);
                    timings.Add(timer.Elapsed.TotalMilliseconds);
                }
            }

            timings.Sort();
            double p50 = timings[(int)Math.Ceiling(timings.Count * 0.50) - 1];
            double p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
            output.WriteLine($"{timings.Count} table_aggregate calls on {Rows} rows: p50 {p50:0} ms, p95 {p95:0} ms, max {timings[^1]:0} ms");
            p95.Should().BeLessThan(Limit.TotalMilliseconds);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    /// <summary>The shapes agents sent in the tables-v1 runs: a count, grouped sums and top-k, a filtered average, distinct counts, extremes.</summary>
    private static List<object> Requests(Guid table) =>
    [
        new { table, aggregates = new object[] { new { aggregate = "count" } } },
        new
        {
            table,
            aggregates = new object[] { new { aggregate = "sum", column = "amount" } },
            group_by = new[] { "customer" },
            order_by = new[] { new { aggregate = "sum", column = "amount", direction = "desc" } },
            limit = 10,
        },
        new
        {
            table,
            aggregates = new object[] { new { aggregate = "avg", column = "amount" }, new { aggregate = "count" } },
            where = new { and = new object[] { new { column = "paid", op = "=", value = true }, new { column = "day", op = ">=", value = "2022-01-01" } } },
        },
        new
        {
            table,
            aggregates = new object[] { new { aggregate = "count_distinct", column = "customer" }, new { aggregate = "min", column = "day" }, new { aggregate = "max", column = "amount" } },
        },
        new
        {
            table,
            aggregates = new object[] { new { aggregate = "count" } },
            group_by = new[] { "paid" },
            having = new { aggregate = "count", op = ">=", value = 5 },
        },
    ];

    private static async Task AggregateAsync(McpClient mcp, Guid containerId, object request)
    {
        CallToolResult result = await mcp.CallToolAsync("table_aggregate", new Dictionary<string, object?>
        {
            ["containerId"] = containerId.ToString(),
            ["request"] = JsonSerializer.SerializeToElement(request),
        });
        string text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        text.Should().NotStartWith("Error:", "the request is valid");
    }

    private async Task<Guid> UploadAsync(Guid containerId)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Csv());
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(part, "files", "orders.csv");
        form.Add(new StringContent("/data/"), "path");
        using HttpResponseMessage response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);
        response.EnsureSuccessStatusCode();
        string documentId = (await response.Content.ReadFromJsonAsync<UploadResponse>())!.Documents.Single().DocumentId;

        // Ready also means the text is indexed, so no ingestion work competes with the timed calls.
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            string body = await fixture.AdminClient.GetStringAsync($"/api/containers/{containerId}/files/{documentId}");
            string? status = JsonDocument.Parse(body).RootElement.GetProperty("status").GetString();
            if (status?.StartsWith("Failed", StringComparison.Ordinal) == true)
                throw new InvalidOperationException($"Ingestion failed: {body}");
            if (status == "Ready")
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                TableInfo table = (await scope.ServiceProvider.GetRequiredService<ITableStore>().ListAsync(containerId)).Single();
                table.RowCount.Should().Be(Rows);
                return table.Id;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("The upload wasn't ready within 10 minutes");
    }

    private async Task<Guid> NewContainerAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"gate-{Guid.NewGuid():N}"[..20], null, ContainerKind.Documents));
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

    private static byte[] Csv()
    {
        var csv = new StringBuilder("id,customer,amount,day,paid\n");
        var start = new DateOnly(2020, 1, 1);
        for (int i = 1; i <= Rows; i++)
        {
            csv.Append(i).Append(",customer-").Append(i % 977).Append(',')
               .Append(i % 10_000).Append('.').Append((i % 100).ToString("00")).Append(',')
               .Append(start.AddDays(i % 1_500).ToString("yyyy-MM-dd")).Append(',')
               .Append(i % 3 == 0 ? "true" : "false").Append('\n');
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }
}

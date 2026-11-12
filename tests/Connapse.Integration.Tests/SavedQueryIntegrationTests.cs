using System.Net;
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

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-311: an admin saves a query over a table with a parameter filled from the caller's email; a viewer lists and
/// runs it over REST and the admin over MCP, each getting only their own rows, marked trusted. Goes through the
/// upload, the routes, the tools, the operations, the store and its migration.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SavedQueryIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly string Orders =
        "id,region,owner,amount\n"
        + $"1,east,{SharedWebAppFixture.AdminEmail},10\n"
        + $"2,east,{Principal.Viewer.Email},20\n"
        + $"3,west,{Principal.Viewer.Email},30\n"
        + $"4,east,{Principal.Viewer.Email},40\n";

    [Fact]
    public async Task SavedQuery_SaveListAndRun_AnswersOnlyTheCallersRowsMarkedTrusted()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            Guid orders = await UploadOrdersAsync(containerId);
            await using McpClient mcp = await Surfaces.McpAsync(fixture);
            using (JsonDocument before = JsonDocument.Parse(await CallAsync(mcp, "table_list", new() { ["containerId"] = containerId.ToString() })))
                before.RootElement.TryGetProperty("savedQueries", out _).Should().BeFalse();

            object definition = new
            {
                description = "My orders in a region",
                kind = "read",
                request = new
                {
                    table = orders,
                    columns = new[] { "id", "amount" },
                    where = new
                    {
                        and = new object[]
                        {
                            new { column = "region", op = "=", value = new { param = "region" } },
                            new { column = "owner", op = "=", value = new { param = "me" } },
                        },
                    },
                    order_by = new[] { new { column = "id", direction = "asc" } },
                },
                parameters = new object[]
                {
                    new { name = "region", type = "text", @default = "east" },
                    new { name = "me", type = "text", from_login = "email" },
                },
            };

            using (HttpResponseMessage saved = await fixture.AdminClient.PutAsJsonAsync(
                $"/api/containers/{containerId}/tables/queries/my_orders", definition))
                saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());

            using HttpClient viewer = await Surfaces.RestAsync(fixture, Principal.Viewer);

            using (HttpResponseMessage forbidden = await viewer.PutAsJsonAsync(
                $"/api/containers/{containerId}/tables/queries/mine", definition))
                forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            List<SavedQueryResponse>? listed = await viewer.GetFromJsonAsync<List<SavedQueryResponse>>(
                $"/api/containers/{containerId}/tables/queries");
            listed.Should().ContainSingle().Which.Parameters.Single(p => p.Name == "me").FromLogin.Should().Be("email");

            using (HttpResponseMessage ran = await viewer.PostAsJsonAsync(
                $"/api/containers/{containerId}/tables/queries/my_orders/run", new { region = "east" }))
            {
                ran.StatusCode.Should().Be(HttpStatusCode.OK, await ran.Content.ReadAsStringAsync());
                using JsonDocument body = JsonDocument.Parse(await ran.Content.ReadAsStringAsync());
                body.RootElement.GetProperty("trusted").GetBoolean().Should().BeTrue();
                body.RootElement.GetProperty("query").GetString().Should().Be("my_orders");
                Rows(body.RootElement.GetProperty("result")).Should().Equal("2|20", "4|40");
            }

            using (HttpResponseMessage spoofed = await viewer.PostAsJsonAsync(
                $"/api/containers/{containerId}/tables/queries/my_orders/run", new { me = SharedWebAppFixture.AdminEmail }))
                spoofed.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            using (JsonDocument after = JsonDocument.Parse(await CallAsync(mcp, "table_list", new() { ["containerId"] = containerId.ToString() })))
            {
                after.RootElement.GetProperty("savedQueries").GetInt32().Should().Be(1);
                after.RootElement.GetProperty("notice").GetString().Should().Contain("table_query_list");
            }
            string tools = await CallAsync(mcp, "table_query_list", new() { ["containerId"] = containerId.ToString() });
            tools.Should().Contain("my_orders");
            using (JsonDocument mine = JsonDocument.Parse(await CallAsync(mcp, "table_query_run",
                new() { ["containerId"] = containerId.ToString(), ["name"] = "my_orders" })))
            {
                mine.RootElement.GetProperty("trusted").GetBoolean().Should().BeTrue();
                Rows(mine.RootElement.GetProperty("result")).Should().Equal("1|10");
            }

            // The run goes through the same table rules as table_read: a column an admin hides is gone from it.
            using (HttpResponseMessage hidden = await fixture.AdminClient.PutAsJsonAsync(
                $"/api/containers/{containerId}/tables/{orders}/hidden-columns", new { columns = new[] { "amount" } }))
                hidden.EnsureSuccessStatusCode();
            using (HttpResponseMessage ran = await viewer.PostAsync(
                $"/api/containers/{containerId}/tables/queries/my_orders/run", null))
            {
                ran.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await ran.Content.ReadAsStringAsync()).Should().Contain("amount");
            }

            using (HttpResponseMessage deleted = await fixture.AdminClient.DeleteAsync(
                $"/api/containers/{containerId}/tables/queries/my_orders"))
                deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
            using (HttpResponseMessage gone = await viewer.PostAsync(
                $"/api/containers/{containerId}/tables/queries/my_orders/run", null))
            {
                gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await gone.Content.ReadAsStringAsync()).Should().Contain("saved-query-not-found");
            }
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    private static List<string> Rows(JsonElement result) =>
        result.GetProperty("rows").EnumerateArray()
            .Select(r => string.Join("|", r.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Number
                ? c.GetDecimal().ToString("G29", System.Globalization.CultureInfo.InvariantCulture)
                : c.ToString())))
            .ToList();

    private static async Task<string> CallAsync(McpClient mcp, string tool, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await mcp.CallToolAsync(tool, arguments);
        string text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        text.Should().NotStartWith("Error:", "the call is valid");
        return text;
    }

    private async Task<Guid> UploadOrdersAsync(Guid containerId)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(Orders));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(part, "files", "orders.csv");
        form.Add(new StringContent("/data/"), "path");
        using HttpResponseMessage response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);
        response.EnsureSuccessStatusCode();
        string documentId = (await response.Content.ReadFromJsonAsync<UploadResponse>())!.Documents.Single().DocumentId;

        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            string body = await fixture.AdminClient.GetStringAsync($"/api/containers/{containerId}/files/{documentId}");
            string? status = JsonDocument.Parse(body).RootElement.GetProperty("status").GetString();
            if (status?.StartsWith("Failed", StringComparison.Ordinal) == true)
                throw new InvalidOperationException($"Ingestion failed: {body}");
            if (status == "Ready")
                break;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The upload wasn't ready within 90 s");
            await Task.Delay(250);
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ITableStore>().ListAsync(containerId)).Single().Id;
    }

    private async Task<Guid> NewContainerAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"sq-{Guid.NewGuid():N}"[..20], null, ContainerKind.Documents));
        return Guid.Parse(created.Id);
    }

    private async Task DeleteContainerAsync(Guid containerId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        foreach (var document in await scope.ServiceProvider.GetRequiredService<IDocumentStore>().ListAsync(containerId, take: int.MaxValue))
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}/files/{document.Id}");
        await scope.ServiceProvider.GetRequiredService<IContainerStore>().DeleteAsync(containerId);
    }
}

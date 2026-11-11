using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Connapse.Application.Containers;
using Connapse.Application.Files;
using Connapse.Application.Tables;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Endpoints;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-312: a table found in an uploaded document is named, with its id, in the passages that held it, through REST
/// search, MCP search and the document view; once an admin hides it, no caller sees its note or its id anywhere, and
/// a re-ingest keeps it hidden.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DocumentTableLinkTests(SharedWebAppFixture fixture)
{
    private const string Fruit =
        "# Orchard report\n\nThe orchard shipped fruit this season.\n\n" +
        "| Fruit | Origin | Kilos |\n|---|---|---|\n| persimmon | Okayama | 12 |\n| quince | Isfahan | 7 |\n\n" +
        "Shipping resumes in spring.\n";

    [Fact]
    public async Task Search_TableInADocument_IsNamedToAViewerUntilHiddenAndStaysHiddenAfterReingest()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            string fileId = await UploadAsync(containerId, "orchard.md", Fruit);
            await WaitForReadyAsync(containerId, fileId);
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var table = (await sp.GetRequiredService<ITableStore>().ListByDocumentAsync(containerId, Guid.Parse(fileId))).Single();
            string tableId = table.Id.ToString();
            using HttpClient viewer = await ViewerClientAsync(sp);

            JsonElement shown = await SearchHitAsync(viewer, containerId, fileId);
            shown.GetProperty("content").GetString().Should().Contain("[Table").And.Contain(tableId)
                .And.NotContain("| persimmon |", "the note stands where the table's text was");
            shown.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("id").GetString()).Should().Equal(tableId);
            (await FileTablesAsync(viewer, containerId, fileId)).Should().Equal(tableId);
            (await viewer.PutAsJsonAsync(HidePath(containerId, table.Id), new HideTableRequest(true)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, "only an admin hides a table");

            (await fixture.AdminClient.PutAsJsonAsync(HidePath(containerId, table.Id), new HideTableRequest(true)))
                .EnsureSuccessStatusCode();

            await ShouldNotLeakAsync(sp, viewer, containerId, fileId, tableId);
            using (JsonDocument adminList = JsonDocument.Parse(
                await fixture.AdminClient.GetStringAsync($"/api/containers/{containerId}/files/{fileId}/tables")))
            {
                JsonElement only = adminList.RootElement.EnumerateArray().Single();
                only.GetProperty("id").GetString().Should().Be(tableId);
                only.GetProperty("hidden").GetBoolean().Should().BeTrue("an admin sees the hidden table, marked");
            }

            await sp.GetRequiredService<IKnowledgeIngester>().IngestAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(Fruit.Replace("12", "13"))),
                new IngestionOptions(DocumentId: fileId, FileName: "orchard.md", ContentType: "text/markdown", Path: "/data/orchard.md")
                {
                    Owner = OwnerRef.ForContainer(containerId),
                });

            var after = (await sp.GetRequiredService<ITableStore>().ListByDocumentAsync(containerId, Guid.Parse(fileId))).Single();
            after.Id.Should().Be(table.Id);
            after.Hidden.Should().BeTrue("an admin's hide survives a re-ingest");
            await ShouldNotLeakAsync(sp, viewer, containerId, fileId, tableId);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    private async Task ShouldNotLeakAsync(IServiceProvider sp, HttpClient viewer, Guid containerId, string fileId, string tableId)
    {
        JsonElement hit = await SearchHitAsync(viewer, containerId, fileId);
        hit.GetProperty("content").GetString().Should().Contain("| persimmon |").And.NotContain("[Table");
        hit.GetRawText().Should().NotContain(tableId);
        (await FileTablesAsync(viewer, containerId, fileId)).Should().BeEmpty();

        var accessor = AsViewer(sp);
        string searched = await McpTools.SearchKnowledge(
            sp.GetRequiredService<Connapse.Application.Search.SearchKnowledge>(), accessor, "persimmon Okayama",
            containerId.ToString(), "Keyword", 5);
        searched.Should().Contain("persimmon").And.NotContain(tableId);
        string document = await McpTools.GetDocument(
            sp.GetRequiredService<GetFile>(), sp.GetRequiredService<GetFileContent>(), sp.GetRequiredService<GetFileTables>(),
            sp.GetRequiredService<GetContainer>(), accessor, containerId.ToString(), fileId);
        document.Should().Contain("persimmon").And.NotContain(tableId);
    }

    private static async Task<JsonElement> SearchHitAsync(HttpClient client, Guid containerId, string fileId)
    {
        using HttpResponseMessage response = await client.GetAsync(
            $"/api/containers/{containerId}/search?q=persimmon+Okayama&mode=Keyword&topK=5");
        response.EnsureSuccessStatusCode();
        using JsonDocument found = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return found.RootElement.GetProperty("hits").EnumerateArray()
            .Single(h => h.GetProperty("documentId").GetString() == fileId).Clone();
    }

    private static async Task<List<string?>> FileTablesAsync(HttpClient client, Guid containerId, string fileId)
    {
        using JsonDocument tables = JsonDocument.Parse(
            await client.GetStringAsync($"/api/containers/{containerId}/files/{fileId}/tables"));
        return tables.RootElement.EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();
    }

    private static string HidePath(Guid containerId, Guid tableId) => $"/api/containers/{containerId}/tables/{tableId}/hidden";

    private static IHttpContextAccessor AsViewer(IServiceProvider sp)
    {
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Viewer")], "test")),
        };
        return accessor;
    }

    private async Task<HttpClient> ViewerClientAsync(IServiceProvider sp)
    {
        string email = $"t-{Guid.NewGuid():N}@example.com";
        const string Password = "TableViewerTest1!";
        var users = sp.GetRequiredService<UserManager<Connapse.Identity.Data.Entities.ConnapseUser>>();
        var user = new Connapse.Identity.Data.Entities.ConnapseUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = email,
            CreatedAt = DateTime.UtcNow,
        };
        (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
        await users.AddToRoleAsync(user, "Viewer");

        using var anon = fixture.Factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/v1/auth/token", new { email, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string? token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
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
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        foreach (var document in await scope.ServiceProvider.GetRequiredService<IDocumentStore>().ListAsync(containerId, take: int.MaxValue))
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}/files/{document.Id}");
        await scope.ServiceProvider.GetRequiredService<IContainerStore>().DeleteAsync(containerId);
    }

    private async Task<string> UploadAsync(Guid containerId, string fileName, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(part, "files", fileName);
        form.Add(new StringContent("/data/"), "path");
        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);
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
            await Task.Delay(500);
        }
        throw new TimeoutException($"Document {documentId} was not Ready within 90 s");
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Application.Files;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-279: an append rewrites the stored file through the same-path upload, which must keep the document's id and the
/// date it was added. The unit tests stub the upload's answer, so only a real append through the database proves it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AppendToFileTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Append_ExistingFile_KeepsItsIdAndAddedDateAndMovesItsUpdatedDate()
    {
        Guid containerId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
                .CreateAsync(new CreateContainerRequest($"append-{Guid.NewGuid():N}"[..20]));
            containerId = Guid.Parse(created.Id);
        }

        string documentId = await UploadAsync(containerId, "First entry.");
        await WaitForReadyAsync(containerId, documentId);
        var before = await StoredAsync(documentId);

        var response = await fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{containerId}/files/{documentId}/append", new { text = "\nSecond entry." });
        response.EnsureSuccessStatusCode();
        var appended = (await response.Content.ReadFromJsonAsync<FileAppend>())!;
        appended.DocumentId.Should().Be(documentId);

        await WaitForReadyAsync(containerId, documentId);
        var after = await StoredAsync(documentId);
        after.Id.Should().Be(before.Id);
        after.CreatedAt.Should().Be(before.CreatedAt, "an append is not a new file, so its added date stays");
        FileDates.UpdatedAt(after).Should().BeAfter(FileDates.UpdatedAt(before));
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var documents = await scope.ServiceProvider.GetRequiredService<IDocumentStore>()
                .ListAsync(containerId, pathPrefix: "/log/", take: int.MaxValue);
            documents.Should().ContainSingle().Which.Id.Should().Be(documentId);
        }
        (await fixture.AdminClient.GetStringAsync($"/api/containers/{containerId}/files/{documentId}/content"))
            .Should().Contain("First entry.\nSecond entry.");
    }

    private async Task<Document> StoredAsync(string documentId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IDocumentStore>().GetAsync(documentId))!;
    }

    private async Task WaitForReadyAsync(Guid containerId, string documentId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            string body = await fixture.AdminClient.GetStringAsync($"/api/containers/{containerId}/files/{documentId}");
            string? status = JsonDocument.Parse(body).RootElement.GetProperty("status").GetString();
            if (status == "Ready")
                return;
            if (status == "Failed")
                throw new InvalidOperationException($"Indexing failed: {body}");
            await Task.Delay(250);
        }
        throw new TimeoutException($"{documentId} was not Ready within 90 s");
    }

    private async Task<string> UploadAsync(Guid containerId, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(part, "files", "entries.md");
        form.Add(new StringContent("/log/"), "path");
        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadResponse>())!.Documents.Single().DocumentId;
    }
}

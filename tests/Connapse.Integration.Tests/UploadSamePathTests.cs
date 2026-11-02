using System.Net.Http.Json;
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
/// #812's "23505 duplicate key ... idx_documents_container_path". In v0.3.2 every upload made a new document id and
/// left the row to the indexing job, so uploading a file that was already there (the reporter re-uploaded what a
/// failed bulk upload had written) made the job insert a second row for the path, which the unique index refused.
/// The overwrite also changed the object's timestamp, so the next cloud poll logged "0 new, 1 changed" and re-indexed
/// the existing row: the error was noisy but the document ended up current. #295 fixed it by having the upload
/// upsert the row by path and hand the job the winning id.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class UploadSamePathTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Upload_FileAlreadyAtThatPath_ReusesItsDocumentSoIndexingCannotInsertASecondRow()
    {
        Guid containerId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
                .CreateAsync(new CreateContainerRequest($"same-{Guid.NewGuid():N}"[..20]));
            containerId = Guid.Parse(created.Id);
        }

        var first = await UploadAsync(containerId, "First version.");
        var second = await UploadAsync(containerId, "Second version, after the bulk upload failed.");

        second.DocumentId.Should().Be(first.DocumentId,
            "a job given a new id for a path that already has a document inserts a second row, which the unique path index refuses");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var documents = await scope.ServiceProvider.GetRequiredService<IDocumentStore>()
                .ListAsync(containerId, pathPrefix: "/tmt/", take: int.MaxValue);
            documents.Where(d => d.Path == "/tmt/notes.md").Should().ContainSingle();
        }

        // The upload re-queues the row before it answers, so Ready now means the replacement was indexed.
        await WaitForReadyAsync(containerId, second.DocumentId);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
            await using var ctx = await dbFactory.CreateDbContextAsync();
            Guid documentId = Guid.Parse(second.DocumentId);
            var contents = await ctx.Chunks.Where(c => c.DocumentId == documentId).Select(c => c.Content).ToListAsync();
            contents.Should().Contain(c => c.Contains("Second version"));
            contents.Should().NotContain(c => c.Contains("First version"));
        }
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
                throw new InvalidOperationException($"Indexing the re-upload failed: {body}");
            await Task.Delay(250);
        }
        throw new TimeoutException($"{documentId} was not Ready within 90 s");
    }

    private async Task<UploadedDocumentResponse> UploadAsync(Guid containerId, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(part, "files", "notes.md");
        form.Add(new StringContent("/tmt/"), "path");
        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadResponse>())!.Documents.Single();
    }
}

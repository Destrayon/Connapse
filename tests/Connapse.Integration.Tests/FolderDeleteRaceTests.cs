using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// A folder delete and an upload into that folder, at the same moment: the upload is either kept whole or removed
/// whole, and storage never keeps a file no document points to (CON-86).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FolderDeleteRaceTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task DeleteFolder_ViaApi_RemovesEveryStoredFile()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            (await UploadAsync(containerId, "a.txt", "/docs")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await UploadAsync(containerId, "b.txt", "/docs/deeper")).StatusCode.Should().Be(HttpStatusCode.OK);

            var response = await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}/folders?path=/docs/&cascade=true");
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);

            (await StoredPathsAsync(containerId)).Should().BeEmpty();
            (await RecordedPathsAsync(containerId)).Should().BeEmpty();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task DeleteFile_ViaApi_RemovesItsStoredFile()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            var upload = await UploadAsync(containerId, "a.txt", "/docs");
            upload.StatusCode.Should().Be(HttpStatusCode.OK);
            string documentId = (await upload.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions))!.Documents[0].DocumentId;

            (await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}/files/{documentId}"))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);

            (await StoredPathsAsync(containerId)).Should().BeEmpty();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task FolderStoreDelete_UploadLandedAfterTheCallerListedTheFolder_RemovesItsStoredFileToo()
    {
        // Both folder deletes list the folder's documents first and only then delete the folder's records. An upload
        // that lands in between is not in the caller's list, so the folder store itself must remove its stored file.
        var containerId = await CreateContainerAsync();
        try
        {
            (await UploadAsync(containerId, "late.txt", "/docs")).StatusCode.Should().Be(HttpStatusCode.OK);

            using var scope = fixture.Factory.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<IFolderStore>().DeleteAsync(Guid.Parse(containerId), "/docs/"))
                .Should().BeTrue();

            (await RecordedPathsAsync(containerId)).Should().BeEmpty();
            (await StoredPathsAsync(containerId)).Should().BeEmpty("a document the folder delete removed must not leave its stored file behind");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task StoreInFolder_FolderDeletedAfterTheUploadFoundIt_WritesAndStoresNothing()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var folders = scope.ServiceProvider.GetRequiredService<IFolderStore>();
            var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var folder = await folders.EnsureAsync(Guid.Parse(containerId), "/docs/");

            await folders.DeleteAsync(Guid.Parse(containerId), "/docs/");
            bool wrote = false;
            var stored = await documents.StoreInFolderAsync(DocumentAt(containerId, "/docs/late.txt"), Guid.Parse(folder.Id),
                _ => { wrote = true; return Task.CompletedTask; });

            stored.Should().BeNull("the folder the upload was going into is gone");
            wrote.Should().BeFalse("a refused upload must not have written a file it would then have to clean up");
            (await RecordedPathsAsync(containerId)).Should().BeEmpty();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task DeleteFolder_WhileAnUploadHoldsTheFolder_WaitsAndRemovesTheUploadWhole()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var folders = scope.ServiceProvider.GetRequiredService<IFolderStore>();
            var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var folder = await folders.EnsureAsync(Guid.Parse(containerId), "/docs/");
            var written = new TaskCompletionSource();
            var release = new TaskCompletionSource();

            // The upload writes its file and then stalls before storing its record, holding the folder.
            var upload = documents.StoreInFolderAsync(DocumentAt(containerId, "/docs/held.txt"), Guid.Parse(folder.Id), async token =>
            {
                await WriteAsync(containerId, "/docs/held.txt", "held", token);
                written.SetResult();
                await release.Task;
            });
            await written.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var delete = Task.Run(async () =>
            {
                using var deleteScope = fixture.Factory.Services.CreateScope();
                return await deleteScope.ServiceProvider.GetRequiredService<IFolderStore>().DeleteAsync(Guid.Parse(containerId), "/docs/");
            });
            await WaitUntilALockIsAwaitedAsync();
            delete.IsCompleted.Should().BeFalse("the delete must wait for the upload holding the folder");

            release.SetResult();
            (await upload.WaitAsync(TimeSpan.FromSeconds(30))).Should().NotBeNull("the upload got the folder first");
            (await delete.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeTrue();

            (await RecordedPathsAsync(containerId)).Should().BeEmpty();
            (await StoredPathsAsync(containerId)).Should().BeEmpty("the delete saw the upload and removed it whole");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task UploadRefusedByADelete_ThenUploadToTheSamePath_KeepsTheNewerUploadWhole()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var folders = scope.ServiceProvider.GetRequiredService<IFolderStore>();
            var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

            // Upload A finds the folder, the folder is deleted, upload B recreates it and lands, then A is refused.
            var seenByA = await folders.EnsureAsync(Guid.Parse(containerId), "/docs/");
            await folders.DeleteAsync(Guid.Parse(containerId), "/docs/");
            var seenByB = await folders.EnsureAsync(Guid.Parse(containerId), "/docs/");
            (await documents.StoreInFolderAsync(DocumentAt(containerId, "/docs/same.txt"), Guid.Parse(seenByB.Id),
                token => WriteAsync(containerId, "/docs/same.txt", "from B", token))).Should().NotBeNull();
            (await documents.StoreInFolderAsync(DocumentAt(containerId, "/docs/same.txt"), Guid.Parse(seenByA.Id),
                token => WriteAsync(containerId, "/docs/same.txt", "from A", token))).Should().BeNull();

            (await RecordedPathsAsync(containerId)).Should().Equal("/docs/same.txt");
            (await ReadAsync(containerId, "/docs/same.txt")).Should().Be("from B", "the refused upload must not touch the newer upload's file");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task DeleteFolder_RacedByUploadsIntoIt_LeavesNoStoredFileWithoutARecord()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            for (int round = 0; round < 12; round++)
            {
                string folder = $"/race{round}";
                (await UploadAsync(containerId, "seed.txt", folder)).StatusCode.Should().Be(HttpStatusCode.OK);

                var uploads = Enumerable.Range(0, 3)
                    .Select(i => UploadAsync(containerId, $"late{i}.txt", folder))
                    .ToList();
                var delete = fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}/folders?path={folder}/&cascade=true");
                await Task.WhenAll([.. uploads, delete]);

                (await delete).StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound);
                foreach (var upload in uploads)
                    (await upload).StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
            }

            var stored = await StoredPathsAsync(containerId);
            var recorded = await RecordedPathsAsync(containerId);
            stored.Should().BeEquivalentTo(recorded, "every upload is either kept whole or removed whole");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    private async Task<string> CreateContainerAsync()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { Name = $"fdrace-{Guid.NewGuid():N}"[..20] });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions))!.Id;
    }

    private async Task<HttpResponseMessage> UploadAsync(string containerId, string fileName, string path)
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"Contents of {fileName} in {path}."));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(file, "files", fileName);
        multipart.Add(new StringContent(path), "path");
        return await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", multipart);
    }

    private static Document DocumentAt(string containerId, string path) =>
        new(Guid.NewGuid().ToString(), containerId, path[(path.LastIndexOf('/') + 1)..], "text/plain", path, 4, DateTime.UtcNow, []);

    private async Task WriteAsync(string containerId, string path, string text, CancellationToken ct)
    {
        var connector = (IWritableConnector)fixture.Factory.Services.GetRequiredService<IManagedStorageProvider>().CreateConnector(containerId);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await connector.WriteFileAsync(path, content, "text/plain", ct);
    }

    private async Task<string> ReadAsync(string containerId, string path)
    {
        var connector = fixture.Factory.Services.GetRequiredService<IManagedStorageProvider>().CreateConnector(containerId);
        using var reader = new StreamReader(await connector.ReadFileAsync(path));
        return await reader.ReadToEndAsync();
    }

    /// <summary>Waits until some session is blocked waiting for a row or transaction lock, as the folder delete is.</summary>
    private async Task WaitUntilALockIsAwaitedAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var context = await factory.CreateDbContextAsync();
            var waiting = await context.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE NOT granted AND locktype IN ('tuple', 'transactionid')")
                .ToListAsync();
            if (waiting[0] > 0)
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The folder delete never waited on a lock.");
    }

    private async Task<List<string>> StoredPathsAsync(string containerId)
    {
        var connector = fixture.Factory.Services.GetRequiredService<IManagedStorageProvider>().CreateConnector(containerId);
        try
        {
            return [.. (await connector.ListFilesAsync()).Select(f => f.Path)];
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }
    }

    private async Task<List<string>> RecordedPathsAsync(string containerId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var documents = await scope.ServiceProvider.GetRequiredService<IDocumentStore>()
            .ListAsync(Guid.Parse(containerId), take: int.MaxValue);
        return [.. documents.Select(d => d.Path)];
    }

    private record ContainerDto(string Id, string Name);
    private record UploadResponse(List<UploadedDocument> Documents);
    private record UploadedDocument(string DocumentId);
}

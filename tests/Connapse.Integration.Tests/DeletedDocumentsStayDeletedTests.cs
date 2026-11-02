using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Files;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Folders;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Integration.Tests;

/// <summary>
/// A deleted file or folder stays deleted (#378). An upload stores its file under the container's own prefix; a delete
/// that removed any other key left the file behind, and the next sync found a stored file with no record and indexed
/// it again, so the deleted document came back. The file browser deletes through the same operations these routes run.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DeletedDocumentsStayDeletedTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Past the delete's own attempt, so the removal job takes what it left.</summary>
    private static readonly TimeSpan PastGrace = PostgresFolderStore.InlineRemovalGrace + TimeSpan.FromMinutes(1);

    [Fact]
    public async Task DeleteFile_ThenSync_FileStaysDeleted()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            var uploaded = await UploadAsync(containerId, "notes.txt", "/");
            (await StoredPathsAsync(containerId)).Should().Equal(["/notes.txt"]);

            var deleted = await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}/files/{uploaded.DocumentId}");
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

            await SyncAsync(containerId);
            (await ListPathsAsync(containerId, "/")).Should().BeEmpty("a deleted file does not come back on the next sync");
            (await StoredPathsAsync(containerId)).Should().BeEmpty("the delete removes the file the upload stored");
            (await OwedPathsAsync(containerId)).Should().BeEmpty("a file the delete removed is not still owed a removal");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task DeleteFolder_ThenSync_FolderAndItsFilesStayDeleted()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            await UploadAsync(containerId, "a.txt", "/reports");
            await UploadAsync(containerId, "b.txt", "/reports/2026");
            (await StoredPathsAsync(containerId)).Should().BeEquivalentTo(["/reports/a.txt", "/reports/2026/b.txt"]);

            var deleted = await fixture.AdminClient.DeleteAsync(
                $"/api/containers/{containerId}/folders?path={Uri.EscapeDataString("/reports/")}&cascade=true");
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

            await SyncAsync(containerId);
            (await ListPathsAsync(containerId, "/")).Should().BeEmpty("neither the folder nor its files come back on the next sync");
            (await StoredPathsAsync(containerId)).Should().BeEmpty("the delete removes every file stored under the folder");
            (await OwedPathsAsync(containerId)).Should().BeEmpty("files the delete removed are not still owed a removal");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task DeleteFile_StorageFailsTheRemoval_SyncLeavesItDeletedAndTheJobRemovesTheFile()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            string documentId = await StoreFileAsync(containerId, "notes.txt", "/docs");

            using (var scope = fixture.Factory.Services.CreateScope())
            {
                var delete = ActivatorUtilities.CreateInstance<DeleteFile>(scope.ServiceProvider, StorageFailingDeletes(), AllowsEverything());
                var deleted = await delete.RunAsync(new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest),
                    Guid.Parse(containerId), documentId);
                deleted.Result!.StorageCleanupFailed.Should().BeTrue();
            }

            (await StoredPathsAsync(containerId)).Should().Equal(["/docs/notes.txt"]);
            (await OwedPathsAsync(containerId)).Should().Equal(["/docs/notes.txt"], "the removal is saved with the delete");

            (await SyncAsync(containerId)).EnqueuedCount.Should().Be(0, "a file owed a removal is not indexed again");
            (await ListPathsAsync(containerId, "/")).Should().BeEmpty("the deleted file does not come back on the next sync");

            await RunRemovalJobAsync();
            (await StoredPathsAsync(containerId)).Should().BeEmpty("the job removed the file the delete couldn't");
            (await OwedPathsAsync(containerId)).Should().BeEmpty();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task DeleteFolder_StorageFailsTheRemovals_SyncLeavesThemDeletedAndTheJobRemovesTheFiles()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            await StoreFileAsync(containerId, "a.txt", "/reports");
            await StoreFileAsync(containerId, "b.txt", "/reports/2026");

            using (var scope = fixture.Factory.Services.CreateScope())
            {
                var folders = new PostgresFolderStore(
                    scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>(),
                    scope.ServiceProvider.GetRequiredService<IDocumentStore>(),
                    StorageFailingDeletes(),
                    NullLogger<PostgresFolderStore>.Instance);
                (await folders.DeleteAsync(Guid.Parse(containerId), "/reports/")).Should().BeTrue();
            }

            (await OwedPathsAsync(containerId)).Should().BeEquivalentTo(["/reports/a.txt", "/reports/2026/b.txt"]);

            (await SyncAsync(containerId)).EnqueuedCount.Should().Be(0, "files owed a removal are not indexed again");
            (await ListPathsAsync(containerId, "/")).Should().BeEmpty("neither the folder nor its files come back on the next sync");

            await RunRemovalJobAsync();
            (await StoredPathsAsync(containerId)).Should().BeEmpty("the job removed the files the delete couldn't");
            (await OwedPathsAsync(containerId)).Should().BeEmpty();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    private static IManagedStorageProvider StorageFailingDeletes()
    {
        var storage = Substitute.For<IManagedStorageProvider>();
        storage.CreateConnector(Arg.Any<string>()).Returns(_ =>
        {
            var failing = Substitute.For<IWritableConnector>();
            failing.DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<Task>(delete => throw new IOException($"Storage refused to delete {delete.Arg<string>()}."));
            return failing;
        });
        return storage;
    }

    private static IOperationAuthorizer AllowsEverything()
    {
        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        return authorizer;
    }

    /// <summary>Runs the removal job against real storage, as if its grace had passed.</summary>
    private async Task RunRemovalJobAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        await new StoredFileRemovalJob(
            scope.ServiceProvider.GetRequiredService<IStoredFileRemovalStore>(),
            scope.ServiceProvider.GetRequiredService<IDocumentStore>(),
            scope.ServiceProvider.GetRequiredService<IManagedStorageProvider>(),
            new ShiftedTime(PastGrace),
            NullLogger<StoredFileRemovalJob>.Instance).RunAsync(CancellationToken.None);
    }

    private async Task<string> CreateContainerAsync()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { Name = $"del378-{Guid.NewGuid():N}"[..20] });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions))!.Id;
    }

    private async Task<UploadedDocument> UploadAsync(string containerId, string fileName, string folder)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"Contents of {fileName} in {folder}."));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "files", fileName);
        content.Add(new StringContent(folder), "path");

        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions))!.Documents.Should().ContainSingle().Subject;
    }

    /// <summary>
    /// Stores a file and its record the way an upload does, but without starting ingestion, so nothing is in flight
    /// when the delete runs.
    /// </summary>
    private async Task<string> StoreFileAsync(string containerId, string fileName, string folder)
    {
        string path = $"{folder.TrimEnd('/')}/{fileName}";
        byte[] content = Encoding.UTF8.GetBytes($"Contents of {fileName} in {folder}.");
        using var scope = fixture.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFolderStore>().EnsureAsync(Guid.Parse(containerId), folder);

        var connector = (IWritableConnector)scope.ServiceProvider.GetRequiredService<IManagedStorageProvider>().CreateConnector(containerId);
        try
        {
            await connector.WriteFileAsync(path, new MemoryStream(content), "text/plain");
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }

        var stored = await scope.ServiceProvider.GetRequiredService<IDocumentStore>().StoreAsync(new Document(
            string.Empty, containerId, fileName, "text/plain", path, content.Length, DateTime.UtcNow, [], Status: DocumentStatus.Ready));
        return stored.DocumentId;
    }

    private async Task<SyncResult> SyncAsync(string containerId)
    {
        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/sync", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SyncResult>(JsonOptions))!;
    }

    private async Task<List<string>> ListPathsAsync(string containerId, string path)
    {
        var response = await fixture.AdminClient.GetAsync($"/api/containers/{containerId}/files?path={Uri.EscapeDataString(path)}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResponse<BrowseEntryDto>>(JsonOptions);
        return [.. page!.Items.Select(e => e.Path)];
    }

    /// <summary>What the container's storage holds, read the way the sync reads it.</summary>
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

    private async Task<List<string>> OwedPathsAsync(string containerId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var removals = scope.ServiceProvider.GetRequiredService<IStoredFileRemovalStore>();
        return [.. await removals.OwedPathsAsync(Guid.Parse(containerId))];
    }

    private sealed class ShiftedTime(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + ahead;
    }

    private record ContainerDto(string Id, string Name);
    private record UploadResponse(IReadOnlyList<UploadedDocument> Documents);
    private record UploadedDocument(string DocumentId, string FileName, string Path);
    private record BrowseEntryDto(string Name, string Path, bool IsFolder);
    private record SyncResult(string BatchId, int TotalFiles, int EnqueuedCount, int SkippedCount);
}

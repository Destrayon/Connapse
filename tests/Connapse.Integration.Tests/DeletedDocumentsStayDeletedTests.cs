using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

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
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
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

    private async Task SyncAsync(string containerId)
    {
        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/sync", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
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

    private record ContainerDto(string Id, string Name);
    private record UploadResponse(IReadOnlyList<UploadedDocument> Documents);
    private record UploadedDocument(string DocumentId, string FileName, string Path);
    private record BrowseEntryDto(string Name, string Path, bool IsFolder);
}

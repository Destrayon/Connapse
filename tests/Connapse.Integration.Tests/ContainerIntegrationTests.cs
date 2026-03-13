using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for container and folder management endpoints,
/// container isolation, and cascade delete behavior.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ContainerIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ── Container CRUD ────────────────────────────────────────────────

    [Fact]
    public async Task CreateContainer_ValidName_Returns201WithContainer()
    {
        // Act
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "test-container", Description = "A test container" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var container = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        container.Should().NotBeNull();
        container!.Name.Should().Be("test-container");
        container.Description.Should().Be("A test container");
        container.DocumentCount.Should().Be(0);
        container.Id.Should().NotBeNullOrEmpty();

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task CreateContainer_DuplicateName_Returns409Conflict()
    {
        // Arrange
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "duplicate-test" });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        // Act
        var duplicateResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "duplicate-test" });

        // Assert
        duplicateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]           // Too short
    [InlineData("-invalid")]    // Starts with hyphen
    [InlineData("invalid-")]    // Ends with hyphen
    [InlineData("has spaces")]  // Spaces not allowed
    [InlineData("UPPERCASE")]   // Uppercase not allowed (#225)
    [InlineData("Mixed-Case")]  // Mixed case not allowed (#225)
    public async Task CreateContainer_InvalidName_Returns400(string name)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { Name = name });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListContainers_ReturnsAllCreatedContainers()
    {
        // Arrange
        var c1 = await CreateContainer("list-test-1");
        var c2 = await CreateContainer("list-test-2");

        // Act
        var response = await fixture.AdminClient.GetAsync("/api/containers?skip=0&take=50");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<ContainerDto>>(JsonOptions);
        paged.Should().NotBeNull();
        paged!.Items.Should().Contain(c => c.Name == "list-test-1");
        paged.Items.Should().Contain(c => c.Name == "list-test-2");

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{c1.Id}");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{c2.Id}");
    }

    [Fact]
    public async Task GetContainer_ExistingId_ReturnsContainerDetails()
    {
        // Arrange
        var container = await CreateContainer("get-test");

        // Act
        var response = await fixture.AdminClient.GetAsync($"/api/containers/{container.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        result.Should().NotBeNull();
        result!.Id.Should().Be(container.Id);
        result.Name.Should().Be("get-test");
        result.DocumentCount.Should().Be(0);

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task GetContainer_NonExistentId_Returns404()
    {
        var response = await fixture.AdminClient.GetAsync($"/api/containers/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteContainer_EmptyContainer_Returns204()
    {
        // Arrange
        var container = await CreateContainer("delete-test");

        // Act
        var response = await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify gone
        var verifyResponse = await fixture.AdminClient.GetAsync($"/api/containers/{container.Id}");
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteContainer_NonExistent_Returns404()
    {
        var response = await fixture.AdminClient.DeleteAsync($"/api/containers/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteContainer_NonEmpty_Returns400()
    {
        // Arrange: Create container and upload a file
        var container = await CreateContainer("nonempty-test");
        var docId = await UploadFile(container.Id, "test.txt", "Some content");
        await WaitForIngestionToComplete(container.Id, docId, timeoutSeconds: 60);

        // Act
        var response = await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");

        // Assert — container has documents, should fail
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Cleanup: delete file, then container
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}/files/{docId}");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    // ── Folder Operations ─────────────────────────────────────────────

    [Fact]
    public async Task CreateFolder_ValidPath_Returns201()
    {
        var container = await CreateContainer("folder-test");

        // Act
        var response = await fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{container.Id}/folders",
            new { Path = "/documents" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var folder = await response.Content.ReadFromJsonAsync<FolderDto>(JsonOptions);
        folder.Should().NotBeNull();
        folder!.Path.Should().Be("/documents/");
        folder.ContainerId.Should().Be(container.Id);

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}/folders?path=/documents/&cascade=true");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task CreateFolder_DuplicatePath_Returns409()
    {
        var container = await CreateContainer("folder-dup-test");

        await fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{container.Id}/folders",
            new { Path = "/docs" });

        // Act
        var response = await fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{container.Id}/folders",
            new { Path = "/docs" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}/folders?path=/docs/&cascade=true");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task CreateFolder_RootPath_Returns400()
    {
        var container = await CreateContainer("folder-root-test");

        var response = await fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{container.Id}/folders",
            new { Path = "/" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task CreateFolder_InNonExistentContainer_Returns404()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{Guid.NewGuid()}/folders",
            new { Path = "/docs" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteFolder_ExistingEmptyFolder_Returns204()
    {
        var container = await CreateContainer("folder-del-test");
        await fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{container.Id}/folders",
            new { Path = "/empty-folder" });

        // Act
        var response = await fixture.AdminClient.DeleteAsync(
            $"/api/containers/{container.Id}/folders?path=/empty-folder/&cascade=true");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    // ── File Browse ───────────────────────────────────────────────────

    [Fact]
    public async Task ListFiles_EmptyContainer_ReturnsEmptyList()
    {
        var container = await CreateContainer("browse-empty-test");

        var response = await fixture.AdminClient.GetAsync($"/api/containers/{container.Id}/files?skip=0&take=200");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<BrowseEntryDto>>(JsonOptions);
        paged.Should().NotBeNull();
        paged!.Items.Should().BeEmpty();

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task ListFiles_WithFoldersAndFiles_ReturnsSorted()
    {
        var container = await CreateContainer("browse-sorted-test");

        // Create folder
        await fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{container.Id}/folders",
            new { Path = "/docs" });

        // Upload file to root
        var docId = await UploadFile(container.Id, "readme.txt", "Hello world");
        await WaitForIngestionToComplete(container.Id, docId, timeoutSeconds: 60);

        // Act
        var response = await fixture.AdminClient.GetAsync($"/api/containers/{container.Id}/files?skip=0&take=200");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<BrowseEntryDto>>(JsonOptions);
        paged.Should().NotBeNull();
        var entries = paged!.Items;
        entries.Should().HaveCountGreaterThanOrEqualTo(2);

        // Folders should come first
        var folderEntries = entries.Where(e => e.IsFolder).ToList();
        var fileEntries = entries.Where(e => !e.IsFolder).ToList();
        folderEntries.Should().NotBeEmpty();
        fileEntries.Should().NotBeEmpty();

        // First entry should be a folder
        entries[0].IsFolder.Should().BeTrue();

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}/files/{docId}");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}/folders?path=/docs/&cascade=true");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    // ── Container Isolation ───────────────────────────────────────────

    [Fact]
    public async Task Search_ContainerIsolation_DoesNotLeakAcrossContainers()
    {
        // Arrange: Two containers with different content
        var containerA = await CreateContainer("isolation-alpha");
        var containerB = await CreateContainer("isolation-beta");

        var docIdA = await UploadFile(containerA.Id, "physics.txt",
            "Quantum entanglement is a phenomenon in quantum mechanics.");
        await WaitForIngestionToComplete(containerA.Id, docIdA, timeoutSeconds: 60);

        var docIdB = await UploadFile(containerB.Id, "cooking.txt",
            "Chocolate souffle requires precise oven temperature control.");
        await WaitForIngestionToComplete(containerB.Id, docIdB, timeoutSeconds: 60);

        // Act & Assert: Search container A finds physics, not cooking
        var searchA = await fixture.AdminClient.GetAsync(
            $"/api/containers/{containerA.Id}/search?q=quantum+entanglement&mode=Keyword&topK=5");
        searchA.StatusCode.Should().Be(HttpStatusCode.OK);
        var resultA = await searchA.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions);
        resultA!.Hits.Should().NotBeEmpty("Container A should find quantum content");
        resultA.Hits.Should().OnlyContain(h => h.DocumentId == docIdA,
            "All results should be from container A");

        // Act & Assert: Search container B finds cooking, not physics
        var searchB = await fixture.AdminClient.GetAsync(
            $"/api/containers/{containerB.Id}/search?q=chocolate+souffle&mode=Keyword&topK=5");
        searchB.StatusCode.Should().Be(HttpStatusCode.OK);
        var resultB = await searchB.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions);
        resultB!.Hits.Should().NotBeEmpty("Container B should find cooking content");
        resultB.Hits.Should().OnlyContain(h => h.DocumentId == docIdB,
            "All results should be from container B");

        // Act & Assert: Search container A for cooking content returns empty
        var crossSearch = await fixture.AdminClient.GetAsync(
            $"/api/containers/{containerA.Id}/search?q=chocolate+souffle&mode=Keyword&topK=5");
        crossSearch.StatusCode.Should().Be(HttpStatusCode.OK);
        var crossResult = await crossSearch.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions);
        crossResult!.Hits.Should().BeEmpty("Container A should NOT find container B's content");

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{containerA.Id}/files/{docIdA}");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{containerB.Id}/files/{docIdB}");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{containerA.Id}");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{containerB.Id}");
    }

    // ── Cascade Delete ────────────────────────────────────────────────

    [Fact]
    public async Task DeleteFolder_WithDocuments_CascadesDocumentDeletion()
    {
        var container = await CreateContainer("cascade-test");

        // Create folder and upload file into it
        await fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{container.Id}/folders",
            new { Path = "/reports" });

        var docId = await UploadFile(container.Id, "report.txt", "Annual report data", path: "/reports/");
        await WaitForIngestionToComplete(container.Id, docId, timeoutSeconds: 60);

        // Verify file exists
        var fileResponse = await fixture.AdminClient.GetAsync($"/api/containers/{container.Id}/files/{docId}");
        fileResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act: Delete folder with cascade
        var deleteResponse = await fixture.AdminClient.DeleteAsync(
            $"/api/containers/{container.Id}/folders?path=/reports/&cascade=true");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert: File should be gone too (cascade delete)
        var verifyResponse = await fixture.AdminClient.GetAsync($"/api/containers/{container.Id}/files/{docId}");
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Container should now be empty and deletable
        var containerDeleteResponse = await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
        containerDeleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── File Upload & Get ─────────────────────────────────────────────

    [Fact]
    public async Task UploadFile_ToContainer_ReturnsDocumentId()
    {
        var container = await CreateContainer("upload-test");

        // Act
        var docId = await UploadFile(container.Id, "test-upload.txt", "Test content for upload");

        // Assert
        docId.Should().NotBeNullOrEmpty();

        await WaitForIngestionToComplete(container.Id, docId, timeoutSeconds: 60);

        // Verify file details
        var response = await fixture.AdminClient.GetAsync($"/api/containers/{container.Id}/files/{docId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = await response.Content.ReadFromJsonAsync<DocumentDto>(JsonOptions);
        document.Should().NotBeNull();
        document!.Id.Should().Be(docId);
        document.FileName.Should().Be("test-upload.txt");
        document.ContainerId.Should().Be(container.Id);

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}/files/{docId}");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task UploadFile_ToNonExistentContainer_Returns404()
    {
        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("test"u8.ToArray());
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(fileContent, "files", "test.txt");

        var response = await fixture.AdminClient.PostAsync(
            $"/api/containers/{Guid.NewGuid()}/files", multipart);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteFile_ExistingFile_Returns204AndRemovesChunks()
    {
        var container = await CreateContainer("file-delete-test");
        var docId = await UploadFile(container.Id, "to-delete.txt",
            "This file will be deleted after ingestion.");
        await WaitForIngestionToComplete(container.Id, docId, timeoutSeconds: 60);

        // Verify search finds it first
        var searchBefore = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/search?q=deleted+after+ingestion&mode=Keyword&topK=5");
        var resultBefore = await searchBefore.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions);
        resultBefore!.Hits.Should().NotBeEmpty();

        // Act: Delete file
        var deleteResponse = await fixture.AdminClient.DeleteAsync(
            $"/api/containers/{container.Id}/files/{docId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert: File gone
        var verifyResponse = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/files/{docId}");
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Assert: Search no longer finds it (chunks cascaded)
        var searchAfter = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/search?q=deleted+after+ingestion&mode=Keyword&topK=5");
        var resultAfter = await searchAfter.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions);
        resultAfter!.Hits.Should().BeEmpty("Chunks should be cascade-deleted with the document");

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<ContainerDto> CreateContainer(string name)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = name });
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Failed to create container '{name}'");
        var container = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        return container!;
    }

    private async Task<string> UploadFile(string containerId, string fileName, string content, string? path = null)
    {
        using var multipart = new MultipartFormDataContent();
        var fileBytes = Encoding.UTF8.GetBytes(content);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(fileContent, "files", fileName);

        if (!string.IsNullOrEmpty(path))
            multipart.Add(new StringContent(path), "path");

        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", multipart);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Failed to upload '{fileName}' to container {containerId}");

        var result = await response.Content.ReadFromJsonAsync<UploadResponseDto>(JsonOptions);
        return result!.Documents.First().DocumentId;
    }

    private async Task WaitForIngestionToComplete(string containerId, string documentId, int timeoutSeconds)
    {
        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        while (DateTime.UtcNow - startTime < timeout)
        {
            var docResponse = await fixture.AdminClient.GetAsync(
                $"/api/containers/{containerId}/files/{documentId}");
            if (docResponse.IsSuccessStatusCode)
            {
                var doc = await docResponse.Content.ReadFromJsonAsync<DocumentDto>(JsonOptions);
                if (doc?.Metadata != null)
                {
                    if (doc.Metadata.TryGetValue("Status", out var status))
                    {
                        if (status == "Failed")
                        {
                            var error = doc.Metadata.GetValueOrDefault("ErrorMessage", "Unknown error");
                            throw new Exception($"Ingestion failed for {documentId}: {error}");
                        }

                        if (status == "Ready"
                            && doc.Metadata.TryGetValue("ChunkCount", out var chunkStr)
                            && int.TryParse(chunkStr, out var chunkCount) && chunkCount > 0)
                        {
                            await Task.Delay(500);
                            return;
                        }
                    }
                }
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Ingestion did not complete within {timeoutSeconds} seconds");
    }

    // ── Path Filtering (Issue #191) ─────────────────────────────────

    [Fact]
    public async Task ListFiles_WithPathFilter_ReturnsFilesAtPath()
    {
        var container = await CreateContainer("path-filter-test");

        // Create folder, then upload file into it
        await fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{container.Id}/folders",
            new { Path = "/docs" });

        var docId = await UploadFile(container.Id, "test.md", "Hello from docs folder", path: "/docs");
        await WaitForIngestionToComplete(container.Id, docId, timeoutSeconds: 60);

        // Verify the document was stored with the right path
        var docResponse = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/files/{docId}");
        docResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = await docResponse.Content.ReadFromJsonAsync<DocumentDto>(JsonOptions);
        doc!.Path.Should().Be("/docs/test.md", "document should be stored with folder path");

        // Act: list files with path filter
        var response = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/files?path=/docs/&skip=0&take=200");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<BrowseEntryDto>>(JsonOptions);
        paged.Should().NotBeNull();
        paged!.Items.Should().Contain(e => e.Name == "test.md" && !e.IsFolder,
            "files uploaded to /docs/ should appear when filtering by ?path=/docs/");

        // Also test without trailing slash
        var response2 = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/files?path=/docs&skip=0&take=200");
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        var paged2 = await response2.Content.ReadFromJsonAsync<PagedResponse<BrowseEntryDto>>(JsonOptions);
        paged2.Should().NotBeNull();
        paged2!.Items.Should().Contain(e => e.Name == "test.md" && !e.IsFolder,
            "path filter should work with and without trailing slash");

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}/files/{docId}");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}/folders?path=/docs/&cascade=true");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    // ── DTOs ──────────────────────────────────────────────────────────

    private record ContainerDto(
        string Id,
        string Name,
        string? Description,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        int DocumentCount);

    private record FolderDto(
        string Id,
        string ContainerId,
        string Path,
        DateTime CreatedAt);

    private record BrowseEntryDto(
        string Name,
        string Path,
        bool IsFolder,
        long? SizeBytes,
        DateTime? LastModified,
        string? Status,
        string? Id);

    private record DocumentDto(
        string Id,
        string ContainerId,
        string FileName,
        string? ContentType,
        string Path,
        long SizeBytes,
        DateTime CreatedAt,
        Dictionary<string, string> Metadata);

    private record UploadResponseDto(
        string? BatchId,
        List<UploadedDocDto> Documents,
        int TotalCount,
        int SuccessCount);

    private record UploadedDocDto(
        string DocumentId,
        string? JobId,
        string FileName,
        long SizeBytes,
        string Path,
        string? Error = null);

    private record SearchResultDto(
        List<SearchHitDto> Hits,
        int TotalMatches,
        TimeSpan Duration);

    private record SearchHitDto(
        string ChunkId,
        string DocumentId,
        string Content,
        float Score,
        Dictionary<string, string> Metadata);
}

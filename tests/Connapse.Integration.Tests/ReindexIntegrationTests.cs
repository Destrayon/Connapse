using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for the reindex service - verifying content-hash detection and re-processing.
/// Updated for container-scoped API (Feature #2).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ReindexIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("aikp_test")
        .WithUsername("aikp_test")
        .WithPassword("aikp_test")
        .Build();

    private readonly MinioContainer _minioContainer = new MinioBuilder()
        .WithImage("minio/minio")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _containerId = null!;

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
        await _minioContainer.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:DefaultConnection", _postgresContainer.GetConnectionString());
                builder.UseSetting("Knowledge:Storage:MinIO:Endpoint", $"{_minioContainer.Hostname}:{_minioContainer.GetMappedPublicPort(9000)}");
                builder.UseSetting("Knowledge:Storage:MinIO:AccessKey", MinioBuilder.DefaultUsername);
                builder.UseSetting("Knowledge:Storage:MinIO:SecretKey", MinioBuilder.DefaultPassword);
                builder.UseSetting("Knowledge:Storage:MinIO:UseSSL", "false");
                builder.UseSetting("Knowledge:Chunking:MaxChunkSize", "200");
                builder.UseSetting("Knowledge:Chunking:MinChunkSize", "10");
                builder.UseSetting("Knowledge:Upload:ParallelWorkers", "1");
            });

        _client = _factory.CreateClient();
        await Task.Delay(2000);

        // Create a container for reindex tests
        var response = await _client.PostAsJsonAsync("/api/containers",
            new { Name = "reindex-tests" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        _containerId = container!.Id;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgresContainer.DisposeAsync();
        await _minioContainer.DisposeAsync();
    }

    [Fact]
    public async Task Reindex_UnchangedDocument_SkipsReprocessing()
    {
        // Arrange: Upload a document
        var originalContent = "This is the original content that should not change.";
        var documentId = await UploadDocument("unchanged-doc.txt", originalContent);

        await WaitForIngestionToComplete(documentId, timeoutSeconds: 60);

        var docBefore = await GetDocument(documentId);
        docBefore.Should().NotBeNull();

        // Verify document is truly ready before reindexing
        var checkBefore = await _client.GetAsync(
            $"/api/containers/{_containerId}/files/{documentId}/reindex-check");
        checkBefore.IsSuccessStatusCode.Should().BeTrue();

        var checkResult = await checkBefore.Content.ReadFromJsonAsync<ReindexCheckDto>(JsonOptions);
        checkResult.Should().NotBeNull();
        checkResult!.NeedsReindex.Should().BeFalse(
            $"Document should be ready. Reason={checkResult.Reason}, Hash={checkResult.CurrentHash}");

        await Task.Delay(2000);

        // Act: Trigger reindex (content unchanged)
        var reindexResponse = await _client.PostAsJsonAsync(
            $"/api/containers/{_containerId}/reindex",
            new { });
        reindexResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var reindexResult = await reindexResponse.Content.ReadFromJsonAsync<ReindexResultDto>(JsonOptions);
        reindexResult.Should().NotBeNull();

        // Assert: Document was not re-enqueued (content hash matches)
        reindexResult!.SkippedCount.Should().Be(1, "Unchanged document should be skipped");
        reindexResult.EnqueuedCount.Should().Be(0, "No documents should be re-enqueued");

        var docAfter = await GetDocument(documentId);
        docAfter.Id.Should().Be(documentId);

        // Cleanup
        await _client.DeleteAsync($"/api/containers/{_containerId}/files/{documentId}");
    }

    [Fact]
    public async Task Reindex_ForceMode_ReprocessesAllDocuments()
    {
        // Arrange: Upload a document
        var content = "Content for force reindex test.";
        var documentId = await UploadDocument("force-doc.txt", content);

        await WaitForIngestionToComplete(documentId, timeoutSeconds: 60);

        // Act: Trigger force reindex
        var reindexResponse = await _client.PostAsJsonAsync(
            $"/api/containers/{_containerId}/reindex",
            new { Force = true });
        reindexResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var reindexResult = await reindexResponse.Content.ReadFromJsonAsync<ReindexResultDto>(JsonOptions);
        reindexResult.Should().NotBeNull();

        // Assert: Document was re-enqueued despite unchanged content
        reindexResult!.EnqueuedCount.Should().Be(1, "Force mode should reindex all documents");
        reindexResult.SkippedCount.Should().Be(0, "Force mode should not skip any documents");

        // Wait for re-ingestion
        await WaitForIngestionToComplete(documentId, timeoutSeconds: 60);

        var docAfter = await GetDocument(documentId);
        docAfter.Id.Should().Be(documentId, "Document should still exist after reindexing");

        // Cleanup
        await _client.DeleteAsync($"/api/containers/{_containerId}/files/{documentId}");
    }

    [Fact]
    public async Task Reindex_ContainerScoped_OnlyReindexesContainerDocuments()
    {
        // Arrange: Create a second container with a document
        var response = await _client.PostAsJsonAsync("/api/containers",
            new { Name = "reindex-other" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var otherContainer = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        var doc1Id = await UploadDocument("container1-doc.txt", "Container 1 document for reindex");
        var doc2Id = await UploadDocumentToContainer(otherContainer!.Id, "container2-doc.txt", "Container 2 document for reindex");

        await WaitForIngestionToComplete(doc1Id, timeoutSeconds: 60);
        await WaitForIngestionToCompleteInContainer(otherContainer.Id, doc2Id, timeoutSeconds: 60);

        // Act: Reindex only the first container (force mode to ensure it does something)
        var reindexResponse = await _client.PostAsJsonAsync(
            $"/api/containers/{_containerId}/reindex",
            new { Force = true });
        reindexResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var reindexResult = await reindexResponse.Content.ReadFromJsonAsync<ReindexResultDto>(JsonOptions);
        reindexResult.Should().NotBeNull();

        // Assert: Only the document in _containerId is reindexed
        reindexResult!.EnqueuedCount.Should().Be(1, "Only the document in the target container should be reindexed");

        // Cleanup
        await _client.DeleteAsync($"/api/containers/{_containerId}/files/{doc1Id}");
        await _client.DeleteAsync($"/api/containers/{otherContainer.Id}/files/{doc2Id}");
        await _client.DeleteAsync($"/api/containers/{otherContainer.Id}");
    }

    private async Task<string> UploadDocument(string fileName, string content)
    {
        return await UploadDocumentToContainer(_containerId, fileName, content);
    }

    private async Task<string> UploadDocumentToContainer(string containerId, string fileName, string content)
    {
        using var multipart = new MultipartFormDataContent();
        var fileBytes = Encoding.UTF8.GetBytes(content);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(fileContent, "files", fileName);
        multipart.Add(new StringContent("/test"), "path");

        var response = await _client.PostAsync($"/api/containers/{containerId}/files", multipart);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions);
        return result!.Documents.First().DocumentId;
    }

    private async Task<DocumentDto> GetDocument(string documentId)
    {
        var response = await _client.GetAsync(
            $"/api/containers/{_containerId}/files/{documentId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = await response.Content.ReadFromJsonAsync<DocumentDto>(JsonOptions);
        return document!;
    }

    private async Task WaitForIngestionToComplete(string documentId, int timeoutSeconds)
    {
        await WaitForIngestionToCompleteInContainer(_containerId, documentId, timeoutSeconds);
    }

    private async Task WaitForIngestionToCompleteInContainer(string containerId, string documentId, int timeoutSeconds)
    {
        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        while (DateTime.UtcNow - startTime < timeout)
        {
            var docResponse = await _client.GetAsync(
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

    // DTOs matching API responses
    private record ContainerDto(string Id, string Name);

    private record UploadResponse(
        string? BatchId,
        List<UploadedDocumentResponse> Documents,
        int TotalCount,
        int SuccessCount);

    private record UploadedDocumentResponse(
        string DocumentId,
        string? JobId,
        string FileName,
        long SizeBytes,
        string Path,
        string? Error = null);

    private record DocumentDto(
        string Id,
        string ContainerId,
        string FileName,
        string? ContentType,
        string Path,
        long SizeBytes,
        DateTime CreatedAt,
        Dictionary<string, string> Metadata);

    private record ReindexResultDto(
        string BatchId,
        int TotalDocuments,
        int EnqueuedCount,
        int SkippedCount,
        int FailedCount,
        Dictionary<string, int> ReasonCounts,
        string Message);

    private record ReindexCheckDto(
        string DocumentId,
        bool NeedsReindex,
        string Reason,
        string? CurrentHash,
        string? StoredHash,
        string? CurrentChunkingStrategy = null,
        string? StoredChunkingStrategy = null,
        string? CurrentEmbeddingModel = null,
        string? StoredEmbeddingModel = null);
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for the reindex service - verifying content-hash detection and re-processing.
/// </summary>
public class ReindexIntegrationTests : IAsyncLifetime
{
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
                builder.UseSetting("Knowledge:Upload:ParallelWorkers", "1");
            });

        _client = _factory.CreateClient();
        await Task.Delay(2000);
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

        await WaitForIngestionToComplete(documentId, timeoutSeconds: 30);

        var docBefore = await GetDocument(documentId);
        docBefore.Should().NotBeNull();

        // Verify document is truly ready before reindexing
        var checkBefore = await _client.GetAsync($"/api/documents/{documentId}/reindex-check");
        if (!checkBefore.IsSuccessStatusCode)
        {
            var errorContent = await checkBefore.Content.ReadAsStringAsync();
            throw new Exception($"Reindex check failed with status {checkBefore.StatusCode}: {errorContent}");
        }

        var checkResult = await checkBefore.Content.ReadFromJsonAsync<ReindexCheckDto>();
        checkResult.Should().NotBeNull();
        if (checkResult!.NeedsReindex)
        {
            throw new Exception($"Document still needs reindex before test: Reason={checkResult.Reason}, Hash={checkResult.CurrentHash}");
        }

        // Give a bit more time for everything to settle
        await Task.Delay(2000);

        // Act: Trigger reindex (content unchanged)
        var reindexResponse = await _client.PostAsync("/api/documents/reindex", null);
        reindexResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var reindexResult = await reindexResponse.Content.ReadFromJsonAsync<ReindexResultDto>();
        reindexResult.Should().NotBeNull();

        // Assert: Document was not re-enqueued (content hash matches)
        // Debug: Output actual counts if test fails
        if (reindexResult!.SkippedCount != 1)
        {
            throw new Exception($"Expected SkippedCount=1, but got SkippedCount={reindexResult.SkippedCount}, EnqueuedCount={reindexResult.EnqueuedCount}, FailedCount={reindexResult.FailedCount}, TotalDocuments={reindexResult.TotalDocuments}. Reasons: {string.Join(", ", reindexResult.ReasonCounts.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        }
        reindexResult!.SkippedCount.Should().Be(1, "Unchanged document should be skipped");
        reindexResult.EnqueuedCount.Should().Be(0, "No documents should be re-enqueued");

        var docAfter = await GetDocument(documentId);
        // Note: Document model doesn't expose LastIndexedAt, can't verify it hasn't changed
        docAfter.Id.Should().Be(documentId);

        // Cleanup
        await _client.DeleteAsync($"/api/documents/{documentId}");
    }

    [Fact]
    public async Task Reindex_ForceMode_ReprocessesAllDocuments()
    {
        // Arrange: Upload a document
        var content = "Content for force reindex test.";
        var documentId = await UploadDocument("force-doc.txt", content);

        await WaitForIngestionToComplete(documentId, timeoutSeconds: 30);

        // Act: Trigger force reindex
        var reindexRequest = new { Force = true };
        var reindexResponse = await _client.PostAsJsonAsync("/api/documents/reindex", reindexRequest);
        reindexResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var reindexResult = await reindexResponse.Content.ReadFromJsonAsync<ReindexResultDto>();
        reindexResult.Should().NotBeNull();

        // Assert: Document was re-enqueued despite unchanged content
        reindexResult!.EnqueuedCount.Should().Be(1, "Force mode should reindex all documents");
        reindexResult.SkippedCount.Should().Be(0, "Force mode should not skip any documents");

        // Wait for re-ingestion
        await WaitForIngestionToComplete(documentId, timeoutSeconds: 30);

        var docAfter = await GetDocument(documentId);
        docAfter.Id.Should().Be(documentId, "Document should still exist after reindexing");

        // Cleanup
        await _client.DeleteAsync($"/api/documents/{documentId}");
    }

    [Fact]
    public async Task Reindex_ByCollection_OnlyReindexesFilteredDocuments()
    {
        // Arrange: Upload documents to different collections
        var doc1Id = await UploadDocument("coll1-doc.txt", "Collection 1 document", collectionId: "collection1");
        var doc2Id = await UploadDocument("coll2-doc.txt", "Collection 2 document", collectionId: "collection2");

        await WaitForIngestionToComplete(doc1Id, timeoutSeconds: 30);
        await WaitForIngestionToComplete(doc2Id, timeoutSeconds: 30);

        // Act: Reindex only collection1
        var reindexRequest = new { CollectionId = "collection1", Force = true };
        var reindexResponse = await _client.PostAsJsonAsync("/api/documents/reindex", reindexRequest);
        reindexResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var reindexResult = await reindexResponse.Content.ReadFromJsonAsync<ReindexResultDto>();
        reindexResult.Should().NotBeNull();

        // Assert: Only collection1 document was reindexed
        reindexResult!.EnqueuedCount.Should().Be(1, "Only documents in collection1 should be reindexed");

        // Cleanup
        await _client.DeleteAsync($"/api/documents/{doc1Id}");
        await _client.DeleteAsync($"/api/documents/{doc2Id}");
    }

    private async Task<string> UploadDocument(string fileName, string content, string? collectionId = null)
    {
        using var multipart = new MultipartFormDataContent();
        var fileBytes = Encoding.UTF8.GetBytes(content);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(fileContent, "file", fileName);

        // Set destination path
        multipart.Add(new StringContent("/test"), "destinationPath");

        // Set collection ID if provided
        if (!string.IsNullOrEmpty(collectionId))
        {
            multipart.Add(new StringContent(collectionId), "collectionId");
        }

        var response = await _client.PostAsync("/api/documents", multipart);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<UploadResponse>();
        return result!.Documents.First().DocumentId;
    }

    private async Task<DocumentDto> GetDocument(string documentId)
    {
        var response = await _client.GetAsync($"/api/documents/{documentId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = await response.Content.ReadFromJsonAsync<DocumentDto>();
        return document!;
    }

    private async Task WaitForIngestionToComplete(string documentId, int timeoutSeconds)
    {
        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        while (DateTime.UtcNow - startTime < timeout)
        {
            // Use reindex-check endpoint to verify document is fully indexed
            var response = await _client.GetAsync($"/api/documents/{documentId}/reindex-check");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var check = await response.Content.ReadFromJsonAsync<ReindexCheckDto>();
                if (check != null)
                {
                    // Document is ready when it doesn't need reindex
                    // (NeedsReindex=false means it's indexed and unchanged)
                    if (!check.NeedsReindex)
                    {
                        await Task.Delay(500); // Give it a moment to settle
                        return;
                    }

                    // If there's an error other than NeverIndexed, throw
                    if (check.Reason == "Error" || check.Reason == "FileNotFound")
                    {
                        throw new Exception($"Ingestion failed for document {documentId}: {check.Reason}");
                    }
                }
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Ingestion did not complete within {timeoutSeconds} seconds");
    }

    // DTOs matching API responses
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
        string VirtualPath,
        string? Error = null);

    private record DocumentDto(
        string Id,
        string FileName,
        string? ContentType,
        string? CollectionId,
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

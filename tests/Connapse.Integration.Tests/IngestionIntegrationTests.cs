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
/// Integration tests for the complete ingestion pipeline: upload → parse → chunk → embed → store → search.
/// Uses Testcontainers to spin up real PostgreSQL and MinIO instances.
/// Updated for container-scoped API (Feature #2).
/// </summary>
public class IngestionIntegrationTests : IAsyncLifetime
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
                builder.UseSetting("Knowledge:Chunking:Overlap", "20");
                builder.UseSetting("Knowledge:Upload:ParallelWorkers", "1");
            });

        _client = _factory.CreateClient();
        await Task.Delay(2000);

        // Create a container for ingestion tests
        var response = await _client.PostAsJsonAsync("/api/containers",
            new { Name = "ingestion-tests" });
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
    public async Task UploadIngestSearch_TextFile_EndToEndWorkflow()
    {
        // Arrange
        var fileContent = """
            The quick brown fox jumps over the lazy dog.

            This is a test document for integration testing.
            It contains multiple paragraphs to ensure proper chunking.

            Artificial intelligence and machine learning are transforming software development.
            Knowledge management systems help organizations capture and share information.

            This document should be ingested, chunked, embedded, and made searchable.
            """;
        var fileName = "test-document.txt";

        // Act 1: Upload document
        var documentId = await UploadDocument(fileName, fileContent);
        documentId.Should().NotBeNullOrEmpty();

        // Act 2: Wait for ingestion to complete
        await WaitForIngestionToComplete(documentId, timeoutSeconds: 30);

        // Act 3: Verify document exists
        var docResponse = await _client.GetAsync(
            $"/api/containers/{_containerId}/files/{documentId}");
        docResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = await docResponse.Content.ReadFromJsonAsync<DocumentDto>(JsonOptions);
        document.Should().NotBeNull();
        document!.Id.Should().Be(documentId);
        document.FileName.Should().Be(fileName);
        document.ContainerId.Should().Be(_containerId);

        // Act 4: Search for content from the document
        var searchResponse = await _client.GetAsync(
            $"/api/containers/{_containerId}/search?q=artificial+intelligence+machine+learning&mode=Keyword&topK=10");
        searchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var searchResult = await searchResponse.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions);
        searchResult.Should().NotBeNull();
        searchResult!.Hits.Should().NotBeEmpty();
        searchResult.TotalMatches.Should().BeGreaterThan(0);

        // Assert: Search results contain our document
        var hit = searchResult.Hits.FirstOrDefault(h => h.DocumentId == documentId);
        hit.Should().NotBeNull("Search should return chunks from the uploaded document");
        hit!.Content.Should().ContainEquivalentOf("artificial intelligence");
        hit.Score.Should().BeGreaterThan(0);

        // Act 5: Clean up
        var deleteResponse = await _client.DeleteAsync(
            $"/api/containers/{_containerId}/files/{documentId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify deletion
        var verifyResponse = await _client.GetAsync(
            $"/api/containers/{_containerId}/files/{documentId}");
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UploadIngestSearch_MultipleDocuments_AllSearchable()
    {
        // Arrange
        var documents = new[]
        {
            ("doc1.txt", "PostgreSQL is a powerful open-source relational database."),
            ("doc2.txt", "MinIO provides S3-compatible object storage for cloud-native applications."),
            ("doc3.txt", "Vector databases enable semantic search using embeddings.")
        };

        var documentIds = new List<string>();

        // Act 1: Upload all documents
        foreach (var (fileName, content) in documents)
        {
            var docId = await UploadDocument(fileName, content);
            documentIds.Add(docId);
        }

        // Act 2: Wait for all ingestions to complete
        foreach (var docId in documentIds)
        {
            await WaitForIngestionToComplete(docId, timeoutSeconds: 30);
        }

        // Act 3: Search for each document's unique content
        var searchTerms = new[] { "PostgreSQL", "MinIO", "Vector databases" };

        foreach (var term in searchTerms)
        {
            var searchResponse = await _client.GetAsync(
                $"/api/containers/{_containerId}/search?q={Uri.EscapeDataString(term)}&mode=Keyword&topK=5");
            searchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var searchResult = await searchResponse.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions);
            searchResult.Should().NotBeNull();
            searchResult!.Hits.Should().NotBeEmpty($"Search for '{term}' should return results");

            var hit = searchResult.Hits.FirstOrDefault();
            hit.Should().NotBeNull();
            hit!.Content.Should().Contain(term, "Search result should contain the search term");
        }

        // Cleanup
        foreach (var docId in documentIds)
        {
            await _client.DeleteAsync($"/api/containers/{_containerId}/files/{docId}");
        }
    }

    private async Task<string> UploadDocument(string fileName, string content)
    {
        using var multipart = new MultipartFormDataContent();
        var fileBytes = Encoding.UTF8.GetBytes(content);
        var fileContent2 = new ByteArrayContent(fileBytes);
        fileContent2.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(fileContent2, "files", fileName);
        multipart.Add(new StringContent("/test"), "path");

        var response = await _client.PostAsync(
            $"/api/containers/{_containerId}/files", multipart);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.Documents.Should().HaveCountGreaterThanOrEqualTo(1);
        return result.Documents.First().DocumentId;
    }

    private async Task WaitForIngestionToComplete(string documentId, int timeoutSeconds)
    {
        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        while (DateTime.UtcNow - startTime < timeout)
        {
            var response = await _client.GetAsync(
                $"/api/containers/{_containerId}/files/{documentId}/reindex-check");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var check = await response.Content.ReadFromJsonAsync<ReindexCheckDto>(JsonOptions);
                if (check is { NeedsReindex: false })
                {
                    // Verify chunks are actually queryable before returning
                    var docResponse = await _client.GetAsync(
                        $"/api/containers/{_containerId}/files/{documentId}");
                    if (docResponse.IsSuccessStatusCode)
                    {
                        var doc = await docResponse.Content.ReadFromJsonAsync<DocumentDto>(JsonOptions);
                        if (doc?.ChunkCount > 0)
                        {
                            return;
                        }
                    }
                }

                if (check?.Reason is "Error" or "FileNotFound")
                    throw new Exception($"Ingestion failed for {documentId}: {check.Reason}");
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

    private record ReindexCheckDto(
        string DocumentId,
        bool NeedsReindex,
        string Reason,
        string? CurrentHash,
        string? StoredHash);
}

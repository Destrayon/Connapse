using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for the complete ingestion pipeline: upload → parse → chunk → embed → store → search
/// Uses Testcontainers to spin up real PostgreSQL and MinIO instances.
/// </summary>
public class IngestionIntegrationTests : IAsyncLifetime
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
        // Start containers
        await _postgresContainer.StartAsync();
        await _minioContainer.StartAsync();

        // Create factory with test configuration
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Override connection strings to point to test containers
                    builder.UseSetting("ConnectionStrings:DefaultConnection", _postgresContainer.GetConnectionString());
                    builder.UseSetting("Knowledge:Storage:MinIO:Endpoint", $"{_minioContainer.Hostname}:{_minioContainer.GetMappedPublicPort(9000)}");
                    builder.UseSetting("Knowledge:Storage:MinIO:AccessKey", MinioBuilder.DefaultUsername);
                    builder.UseSetting("Knowledge:Storage:MinIO:SecretKey", MinioBuilder.DefaultPassword);
                    builder.UseSetting("Knowledge:Storage:MinIO:UseSSL", "false");

                    // Use smaller chunk size for faster testing
                    builder.UseSetting("Knowledge:Chunking:MaxChunkSize", "200");
                    builder.UseSetting("Knowledge:Chunking:Overlap", "20");

                    // Reduce worker count for predictable test behavior
                    builder.UseSetting("Knowledge:Upload:ParallelWorkers", "1");
                });
            });

        _client = _factory.CreateClient();

        // Wait for application to be ready and migrations to complete
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

        using var content = new MultipartFormDataContent();
        var fileBytes = Encoding.UTF8.GetBytes(fileContent);
        var fileContent2 = new ByteArrayContent(fileBytes);
        fileContent2.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        content.Add(fileContent2, "file", fileName);
        content.Add(new StringContent("/test"), "virtualPath");

        // Act 1: Upload document
        var uploadResponse = await _client.PostAsync("/api/documents", content);

        // Assert: Upload succeeded
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var uploadResult = await uploadResponse.Content.ReadFromJsonAsync<UploadResponse>();
        uploadResult.Should().NotBeNull();
        uploadResult!.Documents.Should().HaveCount(1);
        uploadResult.SuccessCount.Should().Be(1);

        var uploadedDoc = uploadResult.Documents.First();
        uploadedDoc.FileName.Should().Be(fileName);
        uploadedDoc.DocumentId.Should().NotBeEmpty();
        uploadedDoc.Error.Should().BeNull();

        var documentId = uploadedDoc.DocumentId;

        // Act 2: Wait for ingestion to complete (poll status)
        await WaitForIngestionToComplete(documentId, timeoutSeconds: 30);

        // Act 3: Verify document exists (note: Document model doesn't have Status/ChunkCount)
        var docResponse = await _client.GetAsync($"/api/documents/{documentId}");
        docResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = await docResponse.Content.ReadFromJsonAsync<DocumentDto>();
        document.Should().NotBeNull();
        document!.Id.Should().Be(documentId);
        document.FileName.Should().Be(fileName);

        // Act 4: Search for content from the document
        var searchResponse = await _client.GetAsync("/api/search?q=artificial+intelligence+machine+learning&mode=Keyword&topK=10");
        searchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var searchResult = await searchResponse.Content.ReadFromJsonAsync<SearchResultDto>();
        searchResult.Should().NotBeNull();
        searchResult!.Hits.Should().NotBeEmpty();
        searchResult.TotalMatches.Should().BeGreaterThan(0);

        // Assert: Search results contain our document
        var hit = searchResult.Hits.FirstOrDefault(h => h.DocumentId == documentId);
        hit.Should().NotBeNull("Search should return chunks from the uploaded document");
        hit!.Content.Should().ContainEquivalentOf("artificial intelligence", "Chunk content should contain searched terms");
        hit.Score.Should().BeGreaterThan(0);

        // Act 5: Clean up - delete document
        var deleteResponse = await _client.DeleteAsync($"/api/documents/{documentId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify deletion
        var verifyResponse = await _client.GetAsync($"/api/documents/{documentId}");
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
            var multipart = new MultipartFormDataContent();
            var fileBytes = Encoding.UTF8.GetBytes(content);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
            multipart.Add(fileContent, "file", fileName);
            multipart.Add(new StringContent("/batch-test"), "virtualPath");

            var response = await _client.PostAsync("/api/documents", multipart);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var result = await response.Content.ReadFromJsonAsync<UploadResponse>();
            documentIds.Add(result!.Documents.First().DocumentId);
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
            var searchResponse = await _client.GetAsync($"/api/search?q={Uri.EscapeDataString(term)}&mode=Keyword&topK=5");
            searchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var searchResult = await searchResponse.Content.ReadFromJsonAsync<SearchResultDto>();
            searchResult.Should().NotBeNull();
            searchResult!.Hits.Should().NotBeEmpty($"Search for '{term}' should return results");

            var hit = searchResult.Hits.FirstOrDefault();
            hit.Should().NotBeNull();
            hit!.Content.Should().Contain(term, "Search result should contain the search term");
        }

        // Cleanup
        foreach (var docId in documentIds)
        {
            await _client.DeleteAsync($"/api/documents/{docId}");
        }
    }

    /// <summary>
    /// Waits for ingestion to complete by checking if document exists and can be searched.
    /// Note: Document model doesn't expose Status, so we just wait a bit and verify existence.
    /// </summary>
    private async Task WaitForIngestionToComplete(string documentId, int timeoutSeconds)
    {
        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        while (DateTime.UtcNow - startTime < timeout)
        {
            var response = await _client.GetAsync($"/api/documents/{documentId}");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var document = await response.Content.ReadFromJsonAsync<DocumentDto>();
                if (document != null)
                {
                    // Document exists, assume ingestion completed
                    // In a real scenario, you might want to search for content to verify
                    await Task.Delay(1000); // Give it a moment to index
                    return;
                }
            }

            await Task.Delay(500); // Poll every 500ms
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

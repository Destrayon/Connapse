using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for the complete ingestion pipeline: upload → parse → chunk → embed → store → search.
/// Uses the shared fixture for infrastructure and creates its own named container for isolation.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class IngestionIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SharedWebAppFixture _fixture;
    private string _containerId = null!;

    public IngestionIntegrationTests(SharedWebAppFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        var uniqueName = $"ingestion-{Guid.NewGuid().ToString("N")[..8]}";
        var response = await _fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = uniqueName });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        _containerId = container!.Id;
    }

    public async Task DisposeAsync()
    {
        if (_containerId is not null)
            await _fixture.AdminClient.DeleteAsync($"/api/containers/{_containerId}");
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
        await WaitForIngestionToComplete(documentId, timeoutSeconds: 60);

        // Act 3: Verify document exists
        var docResponse = await _fixture.AdminClient.GetAsync(
            $"/api/containers/{_containerId}/files/{documentId}");
        docResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = await docResponse.Content.ReadFromJsonAsync<DocumentDto>(JsonOptions);
        document.Should().NotBeNull();
        document!.Id.Should().Be(documentId);
        document.FileName.Should().Be(fileName);
        document.ContainerId.Should().Be(_containerId);
        document.Metadata.Should().ContainKey("ChunkCount");
        int.Parse(document.Metadata["ChunkCount"]).Should().BeGreaterThan(0, "Document should have chunks after ingestion");

        // Act 4: Search for content from the document
        var searchResponse = await _fixture.AdminClient.GetAsync(
            $"/api/containers/{_containerId}/search?q={Uri.EscapeDataString("artificial intelligence")}&mode=Keyword&topK=10");
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
        var deleteResponse = await _fixture.AdminClient.DeleteAsync(
            $"/api/containers/{_containerId}/files/{documentId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify deletion
        var verifyResponse = await _fixture.AdminClient.GetAsync(
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
            await WaitForIngestionToComplete(docId, timeoutSeconds: 60);
        }

        // Act 3: Search for each document's unique content
        var searchTerms = new[] { "PostgreSQL", "MinIO", "Vector databases" };

        foreach (var term in searchTerms)
        {
            var searchResponse = await _fixture.AdminClient.GetAsync(
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
            await _fixture.AdminClient.DeleteAsync($"/api/containers/{_containerId}/files/{docId}");
        }
    }

    [Fact]
    public async Task Upload_ZeroByteFile_Returns400()
    {
        // Arrange: create a 0-byte file upload
        using var multipart = new MultipartFormDataContent();
        var emptyContent = new ByteArrayContent(Array.Empty<byte>());
        emptyContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(emptyContent, "files", "empty.txt");

        // Act
        var response = await _fixture.AdminClient.PostAsync(
            $"/api/containers/{_containerId}/files", multipart);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        body!.Error.Should().Be("File must not be empty");
    }

    private record ErrorResponse(string Error);

    private async Task<string> UploadDocument(string fileName, string content)
    {
        using var multipart = new MultipartFormDataContent();
        var fileBytes = Encoding.UTF8.GetBytes(content);
        var fileContent2 = new ByteArrayContent(fileBytes);
        fileContent2.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(fileContent2, "files", fileName);
        multipart.Add(new StringContent("/test"), "path");

        var response = await _fixture.AdminClient.PostAsync(
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
            var docResponse = await _fixture.AdminClient.GetAsync(
                $"/api/containers/{_containerId}/files/{documentId}");
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

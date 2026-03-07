using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for search endpoints covering different search modes,
/// container scoping, and edge cases.
/// Complements the container isolation search tests in ContainerIntegrationTests.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SearchEndpointTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Search_KeywordMode_FindsMatchingContent()
    {
        var container = await CreateContainer("search-keyword-test");
        var docId = await UploadFile(container.Id, "keyword-doc.txt",
            "The mitochondria is the powerhouse of the cell.");
        await WaitForIngestionToComplete(container.Id, docId);

        // Act
        var response = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/search?q=mitochondria+powerhouse&mode=Keyword&topK=5");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions);
        result!.Hits.Should().NotBeEmpty();
        result.Hits.Should().Contain(h => h.Content.Contains("mitochondria", StringComparison.OrdinalIgnoreCase));

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}/files/{docId}");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task Search_HybridMode_ReturnsResults()
    {
        var container = await CreateContainer("search-hybrid-test");
        var docId = await UploadFile(container.Id, "hybrid-doc.txt",
            "Machine learning algorithms improve through experience and data analysis.");
        await WaitForIngestionToComplete(container.Id, docId);

        // Act — hybrid combines keyword + semantic
        var response = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/search?q=machine+learning+algorithms&mode=Hybrid&topK=5");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions);
        result!.Hits.Should().NotBeEmpty("Hybrid search should find matching content");

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}/files/{docId}");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task Search_EmptyQuery_Returns400()
    {
        var container = await CreateContainer("search-empty-test");

        var response = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/search?q=&mode=Keyword&topK=5");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task Search_NonExistentContainer_Returns404()
    {
        var response = await fixture.AdminClient.GetAsync(
            $"/api/containers/{Guid.NewGuid()}/search?q=test&mode=Keyword&topK=5");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Search_NoResults_ReturnsEmptyHits()
    {
        var container = await CreateContainer("search-noresult-test");
        var docId = await UploadFile(container.Id, "noresult-doc.txt",
            "Simple text about gardening and flowers.");
        await WaitForIngestionToComplete(container.Id, docId);

        // Search for something not in the document
        var response = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/search?q=quantum+cryptography+blockchain&mode=Keyword&topK=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions);
        result!.Hits.Should().BeEmpty("No content matches this query");

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}/files/{docId}");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task Search_Unauthenticated_Returns401()
    {
        using var anonClient = fixture.Factory.CreateClient();
        var response = await anonClient.GetAsync(
            $"/api/containers/{Guid.NewGuid()}/search?q=test&mode=Keyword&topK=5");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private async Task<ContainerDto> CreateContainer(string name)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = name });
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Failed to create container '{name}'");
        var container = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        return container!;
    }

    private async Task<string> UploadFile(string containerId, string fileName, string content)
    {
        using var multipart = new MultipartFormDataContent();
        var fileBytes = Encoding.UTF8.GetBytes(content);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(fileContent, "files", fileName);

        var response = await fixture.AdminClient.PostAsync(
            $"/api/containers/{containerId}/files", multipart);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Failed to upload '{fileName}'");

        var result = await response.Content.ReadFromJsonAsync<UploadResponseDto>(JsonOptions);
        return result!.Documents.First().DocumentId;
    }

    private async Task WaitForIngestionToComplete(string containerId, string documentId, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var docResponse = await fixture.AdminClient.GetAsync(
                $"/api/containers/{containerId}/files/{documentId}");
            if (docResponse.IsSuccessStatusCode)
            {
                var doc = await docResponse.Content.ReadFromJsonAsync<DocumentDto>(JsonOptions);
                if (doc?.Metadata?.TryGetValue("Status", out var status) == true)
                {
                    if (status == "Failed")
                        throw new Exception($"Ingestion failed for {documentId}");
                    if (status == "Ready")
                        return;
                }
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"Ingestion did not complete within {timeoutSeconds}s");
    }

    // ── DTOs ──────────────────────────────────────────────────────────

    private record ContainerDto(string Id, string Name, string? Description, DateTime CreatedAt, DateTime UpdatedAt, int DocumentCount);
    private record UploadResponseDto(string? BatchId, List<UploadedDocDto> Documents, int TotalCount, int SuccessCount);
    private record UploadedDocDto(string DocumentId, string? JobId, string FileName, long SizeBytes, string Path, string? Error = null);
    private record DocumentDto(string Id, string ContainerId, string FileName, string? ContentType, string Path, long SizeBytes, DateTime CreatedAt, Dictionary<string, string> Metadata);
    private record SearchResultDto(List<SearchHitDto> Hits, int TotalMatches, TimeSpan Duration);
    private record SearchHitDto(string ChunkId, string DocumentId, string Content, float Score, Dictionary<string, string> Metadata);
}

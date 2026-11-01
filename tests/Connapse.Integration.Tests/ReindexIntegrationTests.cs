using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.FileSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for the reindex service - verifying content-hash detection and re-processing.
/// Uses the shared fixture for infrastructure and creates its own named container for isolation.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ReindexIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SharedWebAppFixture _fixture;
    private string _containerId = null!;

    public ReindexIntegrationTests(SharedWebAppFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        var uniqueName = $"reindex-{Guid.NewGuid().ToString("N")[..8]}";
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
    public async Task Reindex_UnchangedDocument_SkipsReprocessing()
    {
        // Arrange: Upload a document
        var originalContent = "This is the original content that should not change.";
        var documentId = await UploadDocument("unchanged-doc.txt", originalContent);

        await WaitForIngestionToComplete(documentId, timeoutSeconds: 60);

        var docBefore = await GetDocument(documentId);
        docBefore.Should().NotBeNull();

        // Verify document is truly ready before reindexing
        var checkBefore = await _fixture.AdminClient.GetAsync(
            $"/api/containers/{_containerId}/files/{documentId}/reindex-check");
        checkBefore.IsSuccessStatusCode.Should().BeTrue();

        var checkResult = await checkBefore.Content.ReadFromJsonAsync<ReindexCheckDto>(JsonOptions);
        checkResult.Should().NotBeNull();
        checkResult!.NeedsReindex.Should().BeFalse(
            $"Document should be ready. Reason={checkResult.Reason}, Hash={checkResult.CurrentHash}");

        await Task.Delay(2000);

        // Act: Trigger reindex (content unchanged)
        var reindexResponse = await _fixture.AdminClient.PostAsJsonAsync(
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
        await _fixture.AdminClient.DeleteAsync($"/api/containers/{_containerId}/files/{documentId}");
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(null, "not-a-document-id")]
    public async Task Reindex_InvalidCapOrCursor_IsRejected(int? maxDocuments, string? continueAfter)
    {
        // #627: a non-positive cap used to mean "no cap", the opposite of what was asked for.
        var response = await _fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{_containerId}/reindex",
            new { MaxDocuments = maxDocuments, ContinueAfter = continueAfter });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reindex_ForceMode_ReprocessesAllDocuments()
    {
        // Arrange: Upload a document
        var content = "Content for force reindex test.";
        var documentId = await UploadDocument("force-doc.txt", content);

        await WaitForIngestionToComplete(documentId, timeoutSeconds: 60);

        // Act: Trigger force reindex
        var reindexResponse = await _fixture.AdminClient.PostAsJsonAsync(
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
        await _fixture.AdminClient.DeleteAsync($"/api/containers/{_containerId}/files/{documentId}");
    }

    [Fact]
    public async Task Reindex_ContainerScoped_OnlyReindexesContainerDocuments()
    {
        // Arrange: Create a second container with a document
        var response = await _fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "reindex-other" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var otherContainer = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        var doc1Id = await UploadDocument("container1-doc.txt", "Container 1 document for reindex");
        var doc2Id = await UploadDocumentToContainer(otherContainer!.Id, "container2-doc.txt", "Container 2 document for reindex");

        await WaitForIngestionToComplete(doc1Id, timeoutSeconds: 60);
        await WaitForIngestionToCompleteInContainer(otherContainer.Id, doc2Id, timeoutSeconds: 60);

        // Act: Reindex only the first container (force mode to ensure it does something)
        var reindexResponse = await _fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{_containerId}/reindex",
            new { Force = true });
        reindexResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var reindexResult = await reindexResponse.Content.ReadFromJsonAsync<ReindexResultDto>(JsonOptions);
        reindexResult.Should().NotBeNull();

        // Assert: Only the document in _containerId is reindexed
        reindexResult!.EnqueuedCount.Should().Be(1, "Only the document in the target container should be reindexed");

        // Cleanup
        await _fixture.AdminClient.DeleteAsync($"/api/containers/{_containerId}/files/{doc1Id}");
        await _fixture.AdminClient.DeleteAsync($"/api/containers/{otherContainer.Id}/files/{doc2Id}");
        await _fixture.AdminClient.DeleteAsync($"/api/containers/{otherContainer.Id}");
    }

    [Fact]
    public async Task Reindex_UploadedFileChangedInContainerStorage_IndexesTheNewContent()
    {
        // CON-93: a container keeps its files under its own storage prefix (#957 found deletes
        // missing it). The object keys are written straight to the bucket, not through the
        // connector under test, so a connector that lost the prefix could not pass: the new text
        // exists only under the prefix, and a decoy waits at the unprefixed key.
        var documentId = await UploadDocument("stored-doc.txt", "Original wording about glaciers.");
        await WaitForIngestionToComplete(documentId, timeoutSeconds: 60);
        var doc = await GetDocument(documentId);

        var s3 = _fixture.Factory.Services.GetRequiredService<IAmazonS3>();
        string bucket = _fixture.Factory.Services.GetRequiredService<IOptions<MinioOptions>>().Value.BucketName;
        string unprefixedKey = doc.Path.TrimStart('/');
        string containerKey = $"{_containerId}/{unprefixedKey}";

        await s3.GetObjectMetadataAsync(bucket, containerKey); // the upload landed under the prefix
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = containerKey,
            ContentBody = "Replacement wording about marmalade.",
        });
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = unprefixedKey,
            ContentBody = "Decoy wording about tangerines.",
        });

        var reindexResponse = await _fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{_containerId}/reindex",
            new { });
        reindexResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var reindexResult = await reindexResponse.Content.ReadFromJsonAsync<ReindexResultDto>(JsonOptions);

        reindexResult!.EnqueuedCount.Should().Be(1);
        reindexResult.ReasonCounts.Should().ContainKey(nameof(ReindexReason.ContentChanged),
            "the reindex hashed the file in the container's storage and saw it change");

        await WaitForIngestionToComplete(documentId, timeoutSeconds: 60);

        var search = await _fixture.AdminClient.GetAsync(
            $"/api/containers/{_containerId}/search?q=marmalade&mode=Keyword&topK=5");
        search.StatusCode.Should().Be(HttpStatusCode.OK);
        var hits = (await search.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions))!.Hits;
        hits.Should().Contain(h => h.DocumentId == documentId && h.Content.Contains("marmalade"));

        var stale = await _fixture.AdminClient.GetAsync(
            $"/api/containers/{_containerId}/search?q=glaciers&mode=Keyword&topK=5");
        (await stale.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions))!.Hits
            .Should().NotContain(h => h.DocumentId == documentId, "the old content was replaced");

        var decoy = await _fixture.AdminClient.GetAsync(
            $"/api/containers/{_containerId}/search?q=tangerines&mode=Keyword&topK=5");
        (await decoy.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions))!.Hits
            .Should().NotContain(h => h.DocumentId == documentId, "the file outside the container's prefix was not read");

        await s3.DeleteObjectAsync(bucket, unprefixedKey);
        await _fixture.AdminClient.DeleteAsync($"/api/containers/{_containerId}/files/{documentId}");
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
        fileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(fileContent, "files", fileName);
        multipart.Add(new StringContent("/test"), "path");

        var response = await _fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", multipart);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions);
        return result!.Documents.First().DocumentId;
    }

    private async Task<DocumentDto> GetDocument(string documentId)
    {
        var response = await _fixture.AdminClient.GetAsync(
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
            var docResponse = await _fixture.AdminClient.GetAsync(
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

    private record SearchResultDto(List<SearchHitDto> Hits, int TotalMatches);

    private record SearchHitDto(string ChunkId, string DocumentId, string Content, float Score);

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

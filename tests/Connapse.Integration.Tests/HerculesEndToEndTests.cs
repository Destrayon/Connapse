using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// End-to-end coverage for the HERCULES container-summary method swap. Verifies the
/// document-clustering path through the real ingestion pipeline + Hangfire workers
/// without requiring a configured LLM provider — the early-return branch in
/// <c>IngestionJobs.PerDocSummaryAsync</c> doesn't need an LLM.
/// </summary>
/// <remarks>
/// Quality-comparison tests between the two methods belong in the LLM-judge eval harness,
/// not here. These tests prove the wiring + state machine works correctly.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class HerculesEndToEndTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task DocumentClusteringMode_PerDocSummaryEarlyReturns_NoSummaryGenerated()
    {
        // Arrange: create container with document-clustering enabled
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"hercules-lazy-{Guid.NewGuid():N}".Substring(0, Math.Min(40, 50)) });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        container.Should().NotBeNull();

        try
        {
            // Enable summaries in document-clustering mode for this container only.
            var overrides = new ContainerSettingsOverrides
            {
                Summary = new SummarySettings
                {
                    Enabled = true,
                    ContainerSummaryMethod = SummaryStrategy.DocumentClustering,
                }
            };
            var putResponse = await fixture.AdminClient.PutAsJsonAsync(
                $"/api/containers/{container!.Id}/settings", overrides);
            putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            // Act: upload a document.
            string documentId = await UploadDocumentAsync(container.Id, "lazy-doc.txt",
                "Document content for document-clustering integration test. " +
                "This content should never be passed to an LLM summarizer because the " +
                "container is in document-clustering mode.");

            // Wait for both ingestion AND the PerDocSummary job to finish.
            // In document-clustering mode, PerDocSummary early-returns and transitions to
            // SummaryIndexed without writing a summary.
            Document doc = await WaitForIngestionStateAsync(
                documentId, IngestionState.SummaryIndexed, timeoutSeconds: 60);

            // Assert: the document reached SummaryIndexed without a summary text.
            doc.Summary.Should().BeNullOrEmpty(
                "document-clustering mode must not generate per-doc summaries at ingest");
            doc.SummaryContentHash.Should().BeNullOrEmpty();
            doc.SummaryGeneratedAt.Should().BeNull();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<string> UploadDocumentAsync(string containerId, string fileName, string content)
    {
        using var multipart = new MultipartFormDataContent();
        var byteContent = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        byteContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(byteContent, "files", fileName);
        multipart.Add(new StringContent("/test"), "path");

        var response = await fixture.AdminClient.PostAsync(
            $"/api/containers/{containerId}/files", multipart);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"upload body: {body}");

        var upload = await response.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions);
        upload.Should().NotBeNull();
        upload!.Documents.Should().NotBeEmpty();
        return upload.Documents[0].DocumentId;
    }

    private async Task<Document> WaitForIngestionStateAsync(
        string documentId, IngestionState expected, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var docStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            Document? doc = await docStore.GetAsync(documentId, CancellationToken.None);
            if (doc is not null)
            {
                if (doc.IngestionState == expected)
                    return doc;
                if (doc.Metadata.GetValueOrDefault("Status") == "Failed")
                    throw new Exception(
                        $"Document {documentId} failed: " +
                        $"{doc.Metadata.GetValueOrDefault("ErrorMessage", "Unknown error")}");
            }
            await Task.Delay(250);
        }
        throw new TimeoutException(
            $"Document {documentId} did not reach state '{expected}' within {timeoutSeconds}s");
    }

    // DTOs

    private record ContainerDto(string Id, string Name);
    private record UploadResponse(string? BatchId, List<UploadedDoc> Documents);
    private record UploadedDoc(string DocumentId, string FileName, long SizeBytes);

    private record DocumentDto(
        string Id,
        string ContainerId,
        string FileName,
        string Path,
        long SizeBytes,
        Dictionary<string, string> Metadata);
}

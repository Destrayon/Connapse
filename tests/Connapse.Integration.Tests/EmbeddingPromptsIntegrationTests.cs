using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Pipeline;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Connapse.Integration.Tests;

/// <summary>
/// The default model (nomic-embed-text) has published query/document prompts, so its vectors are
/// stored under a prompt-aware id, and vectors stored before prompts applied stay searchable, with
/// an unprompted query, until a reindex moves them.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class EmbeddingPromptsIntegrationTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Text =
        "Photosynthesis converts light energy into chemical energy stored in glucose inside plant chloroplasts.";

    private string _containerId = null!;

    public async Task InitializeAsync()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"prompts-{Guid.NewGuid().ToString("N")[..8]}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _containerId = (await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions))!.Id;
    }

    public async Task DisposeAsync() => await fixture.AdminClient.DeleteAsync($"/api/containers/{_containerId}");

    [Fact]
    public async Task Upload_PromptedModel_StoresVectorsAndMetadataUnderThePromptAwareId()
    {
        string documentId = await UploadAndWaitAsync();
        string identity = CurrentIdentity();

        identity.Should().NotBe("nomic-embed-text", "the fixture's model has published prompts");
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        await using KnowledgeDbContext ctx = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        List<string> modelIds = await ctx.ChunkVectors
            .Where(v => v.DocumentId == Guid.Parse(documentId)).Select(v => v.ModelId).ToListAsync();
        modelIds.Should().NotBeEmpty().And.OnlyContain(id => id == identity);

        var doc = await ctx.Documents.AsNoTracking().SingleAsync(d => d.Id == Guid.Parse(documentId));
        doc.Metadata[IngestionPipeline.MetadataKeyEmbeddingModel].Should().Be(identity);
    }

    [Fact]
    public async Task LegacyVectors_AreFoundBySemanticSearchAndMarkedForReindex()
    {
        string documentId = await UploadAndWaitAsync();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        // Rewind the document to how an earlier version stored it: bare model id, no prompts.
        await using (KnowledgeDbContext ctx = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync())
        {
            Guid id = Guid.Parse(documentId);
            await ctx.ChunkVectors.Where(v => v.DocumentId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.ModelId, "nomic-embed-text"));
            var doc = await ctx.Documents.SingleAsync(d => d.Id == id);
            doc.Metadata = new Dictionary<string, string>(doc.Metadata)
            {
                [IngestionPipeline.MetadataKeyEmbeddingModel] = "nomic-embed-text",
            };
            await ctx.SaveChangesAsync();
        }

        var search = await fixture.AdminClient.GetFromJsonAsync<SearchResultDto>(
            $"/api/containers/{_containerId}/search?q={Uri.EscapeDataString("how do plants make glucose from light")}&mode=Semantic&topK=5",
            JsonOptions);
        search!.Hits.Should().Contain(h => h.DocumentId == documentId,
            "until a reindex, the query is embedded the way the stored vectors were");

        ReindexCheck check = await scope.ServiceProvider.GetRequiredService<IReindexService>()
            .CheckDocumentAsync(documentId);
        check.Reason.Should().Be(ReindexReason.EmbeddingSettingsChanged);
        check.StoredEmbeddingModel.Should().EndWith(":nomic-embed-text");
        check.CurrentEmbeddingModel.Should().EndWith(":" + CurrentIdentity());
    }

    private string CurrentIdentity() =>
        EmbeddingIdentity.For(fixture.Factory.Services.GetRequiredService<IOptionsMonitor<EmbeddingSettings>>().CurrentValue);


    private async Task<string> UploadAndWaitAsync()
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(Text));
        file.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(file, "files", "photosynthesis.txt");
        multipart.Add(new StringContent("/test"), "path");
        var response = await fixture.AdminClient.PostAsync($"/api/containers/{_containerId}/files", multipart);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string documentId = (await response.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions))!.Documents[0].DocumentId;

        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var doc = await fixture.AdminClient.GetFromJsonAsync<DocumentDto>(
                $"/api/containers/{_containerId}/files/{documentId}", JsonOptions);
            string? status = doc?.Metadata.GetValueOrDefault("Status");
            if (status == "Failed")
                throw new Exception($"Ingestion failed: {doc!.Metadata.GetValueOrDefault("ErrorMessage")}");
            if (status == "Ready")
                return documentId;
            await Task.Delay(500);
        }
        throw new TimeoutException("Ingestion did not complete within 60 seconds");
    }

    private record ContainerDto(string Id, string Name);
    private record UploadResponse(List<UploadedDocument> Documents);
    private record UploadedDocument(string DocumentId);
    private record DocumentDto(string Id, Dictionary<string, string> Metadata);
    private record SearchResultDto(List<SearchHitDto> Hits);
    private record SearchHitDto(string DocumentId);
}

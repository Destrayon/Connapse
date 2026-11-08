using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-277: with ingestion running about 15 small files a minute, a just-uploaded text file answered
/// document_not_ready until indexing reached it, though its text was already stored. It is read at once now, and
/// search, which still has to wait, says how many documents it can't see yet.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ReadBeforeIndexedTests(SharedWebAppFixture fixture)
{
    private HttpClient Admin => fixture.AdminClient;

    [Fact]
    public async Task Content_TextFileStillQueuedForIndexing_IsReadAndSearchSaysItCantFindItYet()
    {
        string containerId = await CreateContainerAsync();
        Guid documentId = await AddStoredTextFileAsync(containerId, "/journal/today.md", "remember: alpha first", DocumentStatus.Queued);

        var read = await Admin.GetAsync($"/api/containers/{containerId}/files/{documentId}/content");

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("content").GetString().Should().Be("remember: alpha first");
        body.GetProperty("awaitingIndex").GetBoolean().Should().BeTrue();

        // Keyword mode, so the search needs no embedding model.
        var searched = await Admin.PostAsJsonAsync($"/api/containers/{containerId}/search", new { query = "alpha", mode = "Keyword" });

        searched.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await searched.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("hits").GetArrayLength().Should().Be(0);
        result.GetProperty("pendingDocuments").GetInt32().Should().Be(1);
    }

    private async Task<string> CreateContainerAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var container = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"unindexed-{Guid.NewGuid():N}"[..20]));
        return container.Id;
    }

    /// <summary>A text file in the container's storage and its row, left at <paramref name="status"/> with no ingestion job.</summary>
    private async Task<Guid> AddStoredTextFileAsync(string containerId, string path, string text, DocumentStatus status)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connector = (IWritableConnector)scope.ServiceProvider.GetRequiredService<IManagedStorageProvider>().CreateConnector(containerId);
        try
        {
            await connector.WriteFileAsync(path, new MemoryStream(Encoding.UTF8.GetBytes(text)), "text/markdown");
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }

        var id = Guid.NewGuid();
        await using var ctx = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        await ctx.Documents.AddAsync(new DocumentEntity
        {
            Id = id,
            ContainerId = Guid.Parse(containerId),
            FileName = path[(path.LastIndexOf('/') + 1)..],
            Path = path,
            ContentHash = "",
            SizeBytes = Encoding.UTF8.GetByteCount(text),
            CreatedAt = DateTime.UtcNow,
            IngestionStatus = status,
            Metadata = [],
        });
        await ctx.SaveChangesAsync();
        return id;
    }
}

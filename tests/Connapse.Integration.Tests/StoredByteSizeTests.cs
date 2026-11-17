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
/// The size the ingestion queue picks a lane by (CON-354). A source's document holds the connector's number until it
/// is first ingested, and for Confluence and Jira that number is a change signature, not a size.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class StoredByteSizeTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task StoredByteSizeAsync_SourceDocument_CountsOnlyOnceIngested()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid notIngested = Guid.NewGuid();
        Guid ingested = Guid.NewGuid();

        await using (var db = await factory.CreateDbContextAsync())
        {
            var connection = new ConnectionEntity
            {
                Id = Guid.NewGuid(),
                Name = $"conn-{Guid.NewGuid():N}",
                Provider = (int)ConnectionProvider.Atlassian,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Connections.Add(connection);
            Guid sourceId = Guid.NewGuid();
            db.Sources.Add(new SourceEntity
            {
                Id = sourceId,
                Name = $"src-{Guid.NewGuid():N}",
                ConnectionId = connection.Id,
                ScopeJson = JsonDocument.Parse("{}"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            // A page's version in the high half of its signature reads as petabytes.
            db.Documents.Add(Document(notIngested, sourceId, "/a.md", sizeBytes: 7L << 32, contentHash: ""));
            db.Documents.Add(Document(ingested, sourceId, "/b.md", sizeBytes: 3000, contentHash: Guid.NewGuid().ToString("N")));
            await db.SaveChangesAsync();
        }

        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        (await store.StoredByteSizeAsync(notIngested.ToString())).Should().BeNull();
        (await store.StoredByteSizeAsync(ingested.ToString())).Should().Be(3000);
        (await store.StoredByteSizeAsync(Guid.NewGuid().ToString())).Should().BeNull();
    }

    private static DocumentEntity Document(Guid id, Guid sourceId, string path, long sizeBytes, string contentHash) => new()
    {
        Id = id,
        SourceId = sourceId,
        FileName = Path.GetFileName(path),
        Path = path,
        SizeBytes = sizeBytes,
        ContentHash = contentHash,
        IngestionStatus = DocumentStatus.Queued,
        CreatedAt = DateTime.UtcNow,
        Metadata = [],
    };
}

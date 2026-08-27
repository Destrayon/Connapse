using System.Text.Json;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Documents;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Which sources still hold documents that no permission rule can be checked against.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DocumentCoordinateReportTests(SharedWebAppFixture fixture)
{
    /// <summary>A source needs a real connection to satisfy the FK, and a scope for the NOT NULL jsonb column.</summary>
    private static async Task<Guid> SeedSourceAsync(KnowledgeDbContext db, Guid sourceId, string sourceName)
    {
        var connection = new ConnectionEntity
        {
            Id = Guid.NewGuid(),
            Name = $"conn-{Guid.NewGuid():N}",
            Provider = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Connections.Add(connection);

        db.Sources.Add(new SourceEntity
        {
            Id = sourceId,
            Name = sourceName,
            ConnectionId = connection.Id,
            ScopeJson = JsonDocument.Parse("{}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        return connection.Id;
    }

    [Fact]
    public async Task UnlocatedBySourceAsync_CountsOnlyDocumentsWithNoResourceUri()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();

        Guid sourceId = Guid.NewGuid();
        string sourceName = $"src-{Guid.NewGuid():N}";

        await using (var db = await factory.CreateDbContextAsync())
        {
            await SeedSourceAsync(db, sourceId, sourceName);

            // Two without a coordinate, one with. Only the two are the operator's problem.
            // Distinct paths: owner_id + path is uniquely indexed, and all three share a source.
            int i = 0;
            foreach (string? uri in new[] { null, null, "s3://acme/located.md" })
            {
                i++;
                db.Documents.Add(new DocumentEntity
                {
                    Id = Guid.NewGuid(),
                    SourceId = sourceId,
                    FileName = $"d{i}.md",
                    Path = $"/d{i}.md",
                    ResourceUri = uri,
                    ContentHash = Guid.NewGuid().ToString("N"),
                    Status = "Ready",
                    CreatedAt = DateTime.UtcNow,
                    Metadata = [],
                });
            }

            await db.SaveChangesAsync();
        }

        var report = new DocumentCoordinateReport(factory);
        var rows = await report.UnlocatedBySourceAsync(CancellationToken.None);

        var row = rows.Should().ContainSingle(r => r.SourceId == sourceId).Subject;
        row.DocumentCount.Should().Be(2);
        row.SourceName.Should().Be(sourceName);
    }

    [Fact]
    public async Task UnlocatedBySourceAsync_OmitsSourcesWhereEveryDocumentIsLocated()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();

        Guid sourceId = Guid.NewGuid();

        await using (var db = await factory.CreateDbContextAsync())
        {
            await SeedSourceAsync(db, sourceId, $"src-{Guid.NewGuid():N}");
            db.Documents.Add(new DocumentEntity
            {
                Id = Guid.NewGuid(),
                SourceId = sourceId,
                FileName = "d.md",
                Path = "/d.md",
                ResourceUri = "s3://acme/located.md",
                ContentHash = Guid.NewGuid().ToString("N"),
                Status = "Ready",
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            });
            await db.SaveChangesAsync();
        }

        var report = new DocumentCoordinateReport(factory);
        var rows = await report.UnlocatedBySourceAsync(CancellationToken.None);

        rows.Should().NotContain(r => r.SourceId == sourceId);
    }
}

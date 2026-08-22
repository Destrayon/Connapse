using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for <see cref="IDocumentStore.FindContainersWithStaleSummariesAsync"/>.
/// Drives the sweep that fires off container summary rollups.
/// </summary>
/// <remarks>
/// Regression coverage: before HERCULES the query filtered to docs with non-null Summary,
/// which silently excluded document-clustering containers (whose docs are mostly
/// null-summary). The sweep would report "no stale containers" forever and rollups would
/// never fire. <see cref="DocumentClusteringContainer_NoPerDocSummaries_StillDetectedAsStale"/>
/// pins the fix.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FindContainersWithStaleSummariesTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task ContainerWithDocsAndNoSummary_IsDetectedAsStale()
    {
        Guid containerId = await SeedContainerAsync("stale-no-summary", containerSummaryAt: null);
        await SeedDocumentAsync(containerId, "/a.txt", summaryAt: null);

        try
        {
            var stale = await GetStaleAsync();
            stale.Should().Contain(containerId);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task DocumentClusteringContainer_NoPerDocSummaries_StillDetectedAsStale()
    {
        // Document-clustering mode: docs have null per-doc Summary. The sweep MUST still detect
        // these containers as stale via doc CreatedAt > container.SummaryGeneratedAt (or
        // container.SummaryGeneratedAt being null on first run).
        Guid containerId = await SeedContainerAsync("stale-doc-clustering", containerSummaryAt: null);
        await SeedDocumentAsync(containerId, "/lazy1.txt", summaryAt: null);
        await SeedDocumentAsync(containerId, "/lazy2.txt", summaryAt: null);

        try
        {
            var stale = await GetStaleAsync();
            stale.Should().Contain(containerId,
                "post-HERCULES, document-clustering containers must be detected even with all null summaries");
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task ContainerWithFreshSummary_NoDocChanges_IsNotStale()
    {
        DateTime now = DateTime.UtcNow;
        Guid containerId = await SeedContainerAsync("not-stale-fresh", containerSummaryAt: now);
        // Doc created BEFORE the container summary → not stale.
        await SeedDocumentAsync(containerId, "/a.txt", summaryAt: null, createdAt: now.AddMinutes(-5));

        try
        {
            var stale = await GetStaleAsync();
            stale.Should().NotContain(containerId);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task ContainerWithFreshSummary_NewDocAdded_IsStale()
    {
        DateTime now = DateTime.UtcNow;
        Guid containerId = await SeedContainerAsync("stale-new-doc", containerSummaryAt: now.AddMinutes(-10));
        // Doc created AFTER the container summary → stale (covers document-clustering re-rollup trigger).
        await SeedDocumentAsync(containerId, "/recent.txt", summaryAt: null, createdAt: now);

        try
        {
            var stale = await GetStaleAsync();
            stale.Should().Contain(containerId);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task ContainerWithFreshSummary_DocReindexedAfter_IsStale()
    {
        DateTime now = DateTime.UtcNow;
        Guid containerId = await SeedContainerAsync("stale-reindex", containerSummaryAt: now.AddMinutes(-10));
        // Doc was created earlier but re-indexed AFTER the container summary → stale.
        await SeedDocumentAsync(
            containerId,
            "/edited.txt",
            summaryAt: null,
            createdAt: now.AddMinutes(-30),
            lastIndexedAt: now);

        try
        {
            var stale = await GetStaleAsync();
            stale.Should().Contain(containerId);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<Guid>> GetStaleAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var docStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        return await docStore.FindContainersWithStaleSummariesAsync(CancellationToken.None);
    }

    private async Task<Guid> SeedContainerAsync(string namePrefix, DateTime? containerSummaryAt)
    {
        Guid id = Guid.NewGuid();
        string name = $"{namePrefix}-{id:N}".Substring(0, Math.Min(40, namePrefix.Length + 12));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factoryDb = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await factoryDb.CreateDbContextAsync();

        ctx.Containers.Add(new ContainerEntity
        {
            Id = id,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SummaryGeneratedAt = containerSummaryAt,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private async Task SeedDocumentAsync(
        Guid containerId,
        string path,
        DateTime? summaryAt,
        DateTime? createdAt = null,
        DateTime? lastIndexedAt = null)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factoryDb = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await factoryDb.CreateDbContextAsync();

        ctx.Documents.Add(new DocumentEntity
        {
            Id = Guid.NewGuid(),
            ContainerId = containerId,
            FileName = path.TrimStart('/'),
            ContentType = "text/plain",
            Path = path,
            ContentHash = $"hash-{Guid.NewGuid():N}",
            SizeBytes = 1,
            ChunkCount = 0,
            Generation = 1,
            Status = "Pending",
            CreatedAt = createdAt ?? DateTime.UtcNow,
            LastIndexedAt = lastIndexedAt,
            SummaryGeneratedAt = summaryAt,
            Metadata = new Dictionary<string, string>(),
        });
        await ctx.SaveChangesAsync();
    }

    private async Task DeleteContainerAsync(Guid containerId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factoryDb = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await factoryDb.CreateDbContextAsync();

        // Delete docs first to satisfy FK.
        var docs = await ctx.Documents.Where(d => d.ContainerId == containerId).ToListAsync();
        ctx.Documents.RemoveRange(docs);
        var container = await ctx.Containers.FirstOrDefaultAsync(c => c.Id == containerId);
        if (container is not null) ctx.Containers.Remove(container);
        await ctx.SaveChangesAsync();
    }
}

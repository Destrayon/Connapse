using System.Net.Http.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Vectors;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pgvector;

namespace Connapse.Integration.Tests;

/// <summary>
/// Per-container vector indexes (#571): containers above the threshold get an HNSW index, the
/// legacy shared indexes and indexes of emptied containers are dropped, and the neighbours-first
/// search returns what exact search returns — including when document filters remove every
/// over-fetched neighbour.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class VectorIndexStrategyTests(SharedWebAppFixture fixture)
{
    private const string Model = "strategy-test-model";

    [Fact]
    public async Task EnsureIndexes_BuildsForLargeContainersAndDropsLegacyAndEmptied()
    {
        Guid large = await CreateContainerAsync("vidx-large");
        Guid small = await CreateContainerAsync("vidx-small");
        try
        {
            await SeedAsync(large, 60, i => "/doc.txt");
            await SeedAsync(small, 5, i => "/doc.txt");
            await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
            await ExecAsync(scope, $"CREATE INDEX idx_cv_emb_legacy_test ON chunk_vectors (model_id) WHERE model_id = '{Model}'");

            VectorColumnManager manager = Manager(scope, threshold: 50);
            await manager.EnsureIndexesAsync();

            List<string> indexes = await IndexesAsync(scope);
            indexes.Should().Contain(VectorColumnManager.GetIndexName(large, Model, 3));
            indexes.Should().NotContain(VectorColumnManager.GetIndexName(small, Model, 3));
            indexes.Should().NotContain("idx_cv_emb_legacy_test");

            await ExecAsync(scope, $"DELETE FROM chunk_vectors WHERE owner_id = '{large}'");
            await manager.EnsureIndexesAsync();
            (await IndexesAsync(scope)).Should().NotContain(VectorColumnManager.GetIndexName(large, Model, 3));
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{large}");
            await fixture.AdminClient.DeleteAsync($"/api/containers/{small}");
        }
    }

    [Fact]
    public async Task Search_WithTheContainerIndex_MatchesExactSearch()
    {
        Guid container = await CreateContainerAsync("vidx-search");
        try
        {
            await SeedAsync(container, 300, i => "/doc.txt");
            await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
            await Manager(scope, threshold: 100).EnsureIndexesAsync();
            (await IndexesAsync(scope)).Should().Contain(VectorColumnManager.GetIndexName(container, Model, 3));

            IVectorStore store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            string index = VectorColumnManager.GetIndexName(container, Model, 3);
            long scansBefore = await IndexScansAsync(scope, index);
            // Between points 7 and 8 but not halfway, so no two points tie on distance.
            float[] query = [MathF.Cos(7.3f / 50f), MathF.Sin(7.3f / 50f), 0.1f];
            IReadOnlyList<VectorSearchResult> hits = await store.SearchAsync(query, 10, Filters(container), SearchScopes.Unrestricted);

            List<string> exact = await ExactTopAsync(scope, container, query, 10);
            hits.Select(h => h.Id).Should().Equal(exact);

            // The search went through the container's index (statistics reach the view within ~1 s).
            long scansAfter = scansBefore;
            for (int attempt = 0; attempt < 20 && scansAfter == scansBefore; attempt++)
            {
                await Task.Delay(250);
                scansAfter = await IndexScansAsync(scope, index);
            }
            scansAfter.Should().BeGreaterThan(scansBefore, "the planner must be able to use the per-container index");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task Search_FilterRemovingEveryNeighbour_FallsBackToExact()
    {
        Guid container = await CreateContainerAsync("vidx-filter");
        try
        {
            // Documents 0–9 are under /rare/; the query sits next to documents 200+, so the
            // over-fetched neighbours (at least 100) contain none of them.
            await SeedAsync(container, 300, i => i < 10 ? "/rare/doc.txt" : "/common/doc.txt");
            await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
            await Manager(scope, threshold: 100).EnsureIndexesAsync();

            IVectorStore store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            Dictionary<string, string> filters = Filters(container);
            filters["pathPrefix"] = "/rare/";
            IReadOnlyList<VectorSearchResult> hits = await store.SearchAsync(Vector(250), 30, filters, SearchScopes.Unrestricted);

            hits.Should().HaveCount(10);
            hits.Should().OnlyContain(h => h.Metadata["path"].StartsWith("/rare/"));
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task IndexedContainer_VectorsOfAnotherSize_StillIngestAndMaintenanceKeepsWorking()
    {
        // The same model id configured with another size: its vectors must not hit the existing
        // index's cast, and two sizes in one container must not break maintenance.
        Guid container = await CreateContainerAsync("vidx-dims");
        try
        {
            await SeedAsync(container, 60, i => "/doc.txt");
            await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
            await Manager(scope, threshold: 50).EnsureIndexesAsync();

            await SeedAsync(container, 60, i => "/wide/doc.txt", dims: 5);
            await Manager(scope, threshold: 50).EnsureIndexesAsync();

            List<string> indexes = await IndexesAsync(scope);
            indexes.Should().Contain(VectorColumnManager.GetIndexName(container, Model, 3));
            indexes.Should().Contain(VectorColumnManager.GetIndexName(container, Model, 5));

            IVectorStore store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            IReadOnlyList<VectorSearchResult> hits = await store.SearchAsync(
                [1f, 0f, 0.1f, 0f, 0f], 10, Filters(container), SearchScopes.Unrestricted);
            hits.Should().HaveCount(10);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    private static Dictionary<string, string> Filters(Guid container) =>
        new() { ["containerId"] = container.ToString(), ["modelId"] = Model };

    /// <summary>Points along a curve, so neighbours by index are neighbours by distance.</summary>
    private static float[] Vector(int i) => [MathF.Cos(i / 50f), MathF.Sin(i / 50f), 0.1f];

    private static VectorColumnManager Manager(AsyncServiceScope scope, int threshold)
    {
        IOptionsMonitor<SearchSettings> settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        settings.CurrentValue.Returns(new SearchSettings { VectorIndexMinVectors = threshold });
        return new VectorColumnManager(
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>(), settings,
            NullLogger<VectorColumnManager>.Instance);
    }

    private async Task SeedAsync(Guid container, int count, Func<int, string> path, int dims = 3)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        await using KnowledgeDbContext ctx = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < count; i++)
        {
            Guid doc = Guid.NewGuid(), chunk = Guid.NewGuid();
            ctx.Documents.Add(new DocumentEntity
            {
                Id = doc, ContainerId = container, FileName = $"{i}.txt", ContentType = "text/plain",
                Path = path(i).Replace("doc.txt", $"{i}.txt"), ContentHash = $"h{i}", SizeBytes = 1, ChunkCount = 1,
                IngestionStatus = DocumentStatus.Ready, StatusChangedAt = now, CreatedAt = now,
            });
            ctx.Chunks.Add(new ChunkEntity { Id = chunk, DocumentId = doc, OwnerId = container, Content = "", TokenCount = 1 });
            ctx.ChunkVectors.Add(new ChunkVectorEntity
            {
                ChunkId = chunk, DocumentId = doc, OwnerId = container,
                Embedding = new Vector(dims == 3 ? Vector(i) : [.. Vector(i), .. new float[dims - 3]]),
                ModelId = Model, Dimensions = dims,
            });
        }
        await ctx.SaveChangesAsync();
    }

    private static async Task<List<string>> ExactTopAsync(AsyncServiceScope scope, Guid container, float[] query, int k)
    {
        await using KnowledgeDbContext ctx = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        return (await ctx.ChunkVectors.AsNoTracking()
                .Where(v => v.OwnerId == container && v.ModelId == Model)
                .Select(v => new { v.ChunkId, v.Embedding })
                .ToListAsync())
            .OrderBy(v => CosineDistance(v.Embedding.ToArray(), query))
            .Take(k)
            .Select(v => v.ChunkId.ToString())
            .ToList();
    }

    private static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return 1 - dot / Math.Sqrt(na * nb);
    }

    private static async Task ExecAsync(AsyncServiceScope scope, string sql)
    {
        await using KnowledgeDbContext ctx = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        await ctx.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task<long> IndexScansAsync(AsyncServiceScope scope, string index)
    {
        await using KnowledgeDbContext ctx = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        return await ctx.Database
            .SqlQueryRaw<long>("SELECT COALESCE(SUM(idx_scan), 0)::bigint AS \"Value\" FROM pg_stat_user_indexes WHERE indexrelname = {0}", index)
            .SingleAsync();
    }

    private static async Task<List<string>> IndexesAsync(AsyncServiceScope scope)
    {
        await using KnowledgeDbContext ctx = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        return await ctx.Database
            .SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'chunk_vectors'")
            .ToListAsync();
    }

    private async Task<Guid> CreateContainerAsync(string prefix)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}" });
        response.EnsureSuccessStatusCode();
        return Guid.Parse((await response.Content.ReadFromJsonAsync<ContainerDto>())!.Id);
    }

    private record ContainerDto(string Id);
}

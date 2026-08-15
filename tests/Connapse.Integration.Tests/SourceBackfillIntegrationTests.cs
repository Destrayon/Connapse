using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Backfill;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourceBackfillIntegrationTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string p) => $"{p}-{Guid.NewGuid():N}"[..20];

    private static async Task<Guid> SeedLegacyContainerAsync(
        KnowledgeDbContext ctx, int connectorType, string config, int documentCount)
    {
        var id = Guid.NewGuid();
        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO containers (id, name, connector_type, connector_config, created_at, updated_at) VALUES ({0}, {1}, {2}, CAST({3} AS jsonb), now(), now())",
            id, ShortName("legacy"), connectorType, config);

        for (int i = 0; i < documentCount; i++)
        {
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, created_at) VALUES (gen_random_uuid(), {0}, NULL, {1}, {2}, '', 1, now())",
                id, $"f{i}.md", $"/f{i}.md");
        }

        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO folders (id, container_id, path, created_at) VALUES (gen_random_uuid(), {0}, '/sub', now())", id);

        return id;
    }

    [Fact]
    public async Task RunAsync_S3Container_BecomesSourceWithSameId()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        await using var ctx = await factory.CreateDbContextAsync();

        Guid containerId = await SeedLegacyContainerAsync(
            ctx, (int)ConnectorType.S3, """{"bucketName":"b","region":"us-east-1"}""", documentCount: 2);

        await backfill.RunAsync(CancellationToken.None);

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var source = await sources.GetAsync(containerId);

        // ID preservation is the whole safety argument: owner_id is unchanged, so
        // chunks and vectors need no rewrite.
        source.Should().NotBeNull();
        source!.DocumentCount.Should().Be(2);

        await using var fresh = await factory.CreateDbContextAsync();
        (await fresh.Containers.AnyAsync(c => c.Id == containerId)).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_RepointsDocumentsAndLeavesOwnerIdUnchanged()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        await using var ctx = await factory.CreateDbContextAsync();

        Guid containerId = await SeedLegacyContainerAsync(
            ctx, (int)ConnectorType.AzureBlob, """{"storageAccountName":"a","containerName":"c"}""", documentCount: 3);

        await backfill.RunAsync(CancellationToken.None);

        await using var fresh = await factory.CreateDbContextAsync();
        var docs = await fresh.Documents.AsNoTracking().Where(d => d.SourceId == containerId).ToListAsync();

        docs.Should().HaveCount(3);
        docs.Should().OnlyContain(d => d.ContainerId == null);
        docs.Should().OnlyContain(d => d.OwnerId == containerId);
    }

    [Fact]
    public async Task RunAsync_DeletesFolderRowsForMigratedContainers()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        await using var ctx = await factory.CreateDbContextAsync();

        Guid containerId = await SeedLegacyContainerAsync(
            ctx, (int)ConnectorType.Filesystem, """{"rootPath":"/data"}""", documentCount: 1);

        await backfill.RunAsync(CancellationToken.None);

        await using var fresh = await factory.CreateDbContextAsync();
        (await fresh.Folders.AnyAsync(f => f.ContainerId == containerId)).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_TwoContainersSameCredential_ShareOneConnection()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        await using var ctx = await factory.CreateDbContextAsync();

        Guid a = await SeedLegacyContainerAsync(ctx, (int)ConnectorType.S3, """{"bucketName":"one","region":"eu-north-1"}""", 1);
        Guid b = await SeedLegacyContainerAsync(ctx, (int)ConnectorType.S3, """{"bucketName":"two","region":"eu-north-1"}""", 1);

        await backfill.RunAsync(CancellationToken.None);

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var sourceA = await sources.GetAsync(a);
        var sourceB = await sources.GetAsync(b);

        sourceA!.ConnectionId.Should().Be(sourceB!.ConnectionId);
        (await connections.GetAsync(sourceA.ConnectionId))!.SourceCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task RunAsync_LeavesManagedContainersAlone()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        await using var ctx = await factory.CreateDbContextAsync();

        var managedId = Guid.NewGuid();
        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO containers (id, name, connector_type, created_at, updated_at) VALUES ({0}, {1}, 0, now(), now())",
            managedId, ShortName("managed"));

        await backfill.RunAsync(CancellationToken.None);

        await using var fresh = await factory.CreateDbContextAsync();
        (await fresh.Containers.AnyAsync(c => c.Id == managedId)).Should().BeTrue();
        (await fresh.Sources.AnyAsync(s => s.Id == managedId)).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_RunTwice_IsIdempotent()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        await using var ctx = await factory.CreateDbContextAsync();

        Guid containerId = await SeedLegacyContainerAsync(
            ctx, (int)ConnectorType.S3, """{"bucketName":"idem","region":"us-west-2"}""", documentCount: 2);

        await backfill.RunAsync(CancellationToken.None);
        var second = await backfill.RunAsync(CancellationToken.None);

        // The second pass finds nothing left to migrate.
        second.ContainersMigrated.Should().Be(0);

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        (await sources.GetAsync(containerId))!.DocumentCount.Should().Be(2);
    }

    [Fact]
    public async Task GetContainer_AfterMigration_StillResolvesByOldId()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        await using var ctx = await factory.CreateDbContextAsync();

        Guid containerId = await SeedLegacyContainerAsync(
            ctx, (int)ConnectorType.S3, """{"bucketName":"compat","region":"us-east-1"}""", documentCount: 1);

        await backfill.RunAsync(CancellationToken.None);

        // The container row is gone, but agent prompts, CLI scripts, and bookmarks still
        // carry this ID. The compatibility read must keep them working.
        var response = await fixture.AdminClient.GetAsync($"/api/containers/{containerId}");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(containerId.ToString());
    }

    [Fact]
    public async Task GetContainer_UnknownId_StillReturns404()
    {
        var response = await fixture.AdminClient.GetAsync($"/api/containers/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }
}

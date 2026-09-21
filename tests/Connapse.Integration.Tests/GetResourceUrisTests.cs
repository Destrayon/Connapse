using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for <see cref="IDocumentStore.GetResourceUrisAsync"/> — the batched lookup
/// the search verifier uses to map ranked hits back to their governing <c>resource_uri</c> in one
/// query instead of N.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class GetResourceUrisTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task SeededIdsAndUnknownId_ResolveToTheirUriOrNull()
    {
        Guid containerId = Guid.NewGuid();
        Guid azureDocId = Guid.NewGuid();
        Guid nonCloudDocId = Guid.NewGuid();
        Guid unknownDocId = Guid.NewGuid();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factoryDb = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();

        try
        {
            await using (var ctx = await factoryDb.CreateDbContextAsync())
            {
                ctx.Containers.Add(new ContainerEntity
                {
                    Id = containerId,
                    Name = $"c-{containerId:N}",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });

                ctx.Documents.Add(new DocumentEntity
                {
                    Id = azureDocId,
                    ContainerId = containerId,
                    FileName = "a.md",
                    Path = "/a.md",
                    ResourceUri = "azblob://acct/docs/a",
                    ContentHash = Guid.NewGuid().ToString("N"),
                    Status = "Ready",
                    CreatedAt = DateTime.UtcNow,
                    Metadata = [],
                });

                ctx.Documents.Add(new DocumentEntity
                {
                    Id = nonCloudDocId,
                    ContainerId = containerId,
                    FileName = "b.md",
                    Path = "/b.md",
                    ResourceUri = null,
                    ContentHash = Guid.NewGuid().ToString("N"),
                    Status = "Ready",
                    CreatedAt = DateTime.UtcNow,
                    Metadata = [],
                });

                await ctx.SaveChangesAsync();
            }

            var docStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var result = await docStore.GetResourceUrisAsync(
                [azureDocId.ToString(), nonCloudDocId.ToString(), unknownDocId.ToString()],
                CancellationToken.None);

            result[azureDocId.ToString()].Should().Be("azblob://acct/docs/a");
            result[nonCloudDocId.ToString()].Should().BeNull();
            result.GetValueOrDefault(unknownDocId.ToString()).Should().BeNull();
        }
        finally
        {
            await using var ctx = await factoryDb.CreateDbContextAsync();
            var docs = await ctx.Documents.Where(d => d.ContainerId == containerId).ToListAsync();
            ctx.Documents.RemoveRange(docs);
            var container = await ctx.Containers.FirstOrDefaultAsync(c => c.Id == containerId);
            if (container is not null) ctx.Containers.Remove(container);
            await ctx.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task EmptyInput_ReturnsEmptyMap()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var docStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var result = await docStore.GetResourceUrisAsync([], CancellationToken.None);

        result.Should().BeEmpty();
    }
}

using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests.Links;

/// <summary>
/// A GitHub record's links reach the link layer as the pipeline ingests it (CON-33): written with its chunks,
/// replaced when it is ingested again, removed when it no longer asserts them or is deleted.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class GitHubLinkIngestionTests(SharedWebAppFixture fixture)
{
    private async Task<(Guid Container, Guid Document)> SeedAsync(IServiceProvider sp)
    {
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid containerId = Guid.NewGuid(), documentId = Guid.NewGuid();
        await using var ctx = await dbFactory.CreateDbContextAsync();
        ctx.Containers.Add(new ContainerEntity
        {
            Id = containerId, Name = $"ghl-{containerId:N}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        ctx.Documents.Add(new DocumentEntity
        {
            Id = documentId, ContainerId = containerId, FileName = "7.md", Path = "/pulls/7.md",
            ContentHash = "", CreatedAt = DateTime.UtcNow, Metadata = [],
        });
        await ctx.SaveChangesAsync();
        return (containerId, documentId);
    }

    private static Task IngestAsync(IServiceProvider sp, Guid container, Guid document, Dictionary<string, string> metadata) =>
        sp.GetRequiredService<IKnowledgeIngester>().IngestAsync(
            new MemoryStream("# octocat/hello #7: Fix the stall\n\nRewrites the warm-up."u8.ToArray()),
            new IngestionOptions(
                DocumentId: document.ToString(), FileName: "7.md", ContentType: "text/markdown",
                ContainerId: container.ToString(), Path: "/pulls/7.md", Strategy: ChunkingStrategy.Record,
                Metadata: metadata));

    private static Dictionary<string, string> Pull(string repo, params (string Key, string Value)[] extra)
    {
        Dictionary<string, string> metadata = new()
        {
            ["github:repo"] = repo, ["github:type"] = "pull_request", ["github:number"] = "7",
        };
        foreach ((string key, string value) in extra)
            metadata["github:" + key] = value;
        return metadata;
    }

    private static async Task<List<(string Type, string To)>> EdgesAsync(IServiceProvider sp, Guid document)
    {
        await using var ctx = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        string id = document.ToString();
        return (await ctx.LinkEdges.AsNoTracking().Where(e => e.DocumentId == id).ToListAsync())
            .Select(e => (e.Type, e.ToNode)).OrderBy(e => e.ToNode, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Ingest_PullRequest_WritesItsLinksAndStandsForItsNode()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        string repo = $"octocat/r{Guid.NewGuid():N}";
        (Guid container, Guid document) = await SeedAsync(sp);

        await IngestAsync(sp, container, document, Pull(repo, ("closes", "3"), ("parent", "2"), ("references", "9")));

        (await EdgesAsync(sp, document)).Should().Equal(
            ("parent", $"gh:{repo}#2"), ("closes", $"gh:{repo}#3"));
        ILookup<string, string> nodes = await sp.GetRequiredService<ILinkIndex>().NodesOfAsync([document.ToString()]);
        nodes[document.ToString()].Should().Equal($"gh:{repo}#7");
        (await sp.GetRequiredService<ILinkIndex>().AnyAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Ingest_AgainWithOtherLinks_ReplacesTheOldOnes()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        string repo = $"octocat/r{Guid.NewGuid():N}";
        (Guid container, Guid document) = await SeedAsync(sp);
        await IngestAsync(sp, container, document, Pull(repo, ("closes", "3")));

        await IngestAsync(sp, container, document, Pull(repo, ("closes", "4")));

        (await EdgesAsync(sp, document)).Should().Equal(("closes", $"gh:{repo}#4"));
    }

    [Fact]
    public async Task Ingest_MetadataNoLongerGitHub_RemovesItsLinks()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        (Guid container, Guid document) = await SeedAsync(sp);
        await IngestAsync(sp, container, document, Pull($"octocat/r{Guid.NewGuid():N}", ("closes", "3")));

        await IngestAsync(sp, container, document, []);

        (await EdgesAsync(sp, document)).Should().BeEmpty();
        (await sp.GetRequiredService<ILinkIndex>().NodesOfAsync([document.ToString()])).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_IngestedRecord_RemovesItsLinks()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        (Guid container, Guid document) = await SeedAsync(sp);
        await IngestAsync(sp, container, document, Pull($"octocat/r{Guid.NewGuid():N}", ("closes", "3")));

        await using (var ctx = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync())
            await ctx.Documents.Where(d => d.Id == document).ExecuteDeleteAsync();

        (await EdgesAsync(sp, document)).Should().BeEmpty();
    }
}
